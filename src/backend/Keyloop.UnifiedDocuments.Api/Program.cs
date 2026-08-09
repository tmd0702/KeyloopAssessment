using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using Keyloop.UnifiedDocuments.Application;
using Keyloop.UnifiedDocuments.Api;
using Keyloop.UnifiedDocuments.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);
Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;
var signingKey = builder.Configuration["Jwt:SigningKey"]
    ?? (builder.Environment.IsDevelopment() ? "local-development-only-jwt-signing-key-at-least-32-bytes" : null)
    ?? throw new InvalidOperationException("JWT signing key must be configured with Jwt__SigningKey outside the Development environment.");
var jwt = new JwtOptions
{
    Issuer = builder.Configuration["Jwt:Issuer"] ?? "keyloop-unified-documents",
    Audience = builder.Configuration["Jwt:Audience"] ?? "keyloop-unified-documents-web",
    SigningKey = signingKey
};
builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration.ReadFrom.Configuration(context.Configuration).ReadFrom.Services(services).Enrich.FromLogContext().Enrich.With<TraceContextEnricher>().WriteTo.Console();
    var otlpLogsEndpoint = context.Configuration["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"];
    if (Uri.TryCreate(otlpLogsEndpoint, UriKind.Absolute, out var endpoint))
        configuration.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = null;
            options.LogsEndpoint = endpoint.ToString();
            options.Protocol = OtlpProtocol.HttpProtobuf;
            options.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = "keyloop-unified-documents-api",
                ["deployment.environment"] = context.HostingEnvironment.EnvironmentName.ToLowerInvariant()
            };
        }, ignoreEnvironment: true);
});
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => JwtAuthentication.Configure(options, jwt));
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("keyloop-unified-documents-api").AddAttributes([new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName.ToLowerInvariant())]))
    .WithTracing(tracing => tracing.AddSource(UnifiedDocumentTelemetry.ActivitySourceName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddMeter(DocumentTelemetry.MeterName, ProviderResultCache.MeterName, SearchOperationsTelemetry.MeterName).AddRuntimeInstrumentation().AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddPrometheusExporter());
builder.Services.AddSingleton(builder.Configuration.GetSection("Search").Get<SearchOptions>() ?? new SearchOptions());
builder.Services.AddSearchOperations(builder.Configuration.GetSection("Audit").Get<AuditOptions>() ?? new AuditOptions());
builder.Services.AddSingleton<IDocumentAggregator, DocumentAggregator>();
builder.Services.AddDocumentProviders(
    new ProviderOptions { BaseAddress = new Uri(builder.Configuration["Providers:Sales:BaseUrl"] ?? "http://localhost:5101/"), Credential = builder.Configuration["Providers:Sales:BearerToken"] ?? "local-sales-token", TimeoutSeconds = builder.Configuration.GetValue("Providers:Sales:TimeoutSeconds", 6), Concurrency = builder.Configuration.GetSection("Providers:Sales:Concurrency").Get<ProviderConcurrencyOptions>() ?? new ProviderConcurrencyOptions() },
    new ProviderOptions { BaseAddress = new Uri(builder.Configuration["Providers:Service:BaseUrl"] ?? "http://localhost:5102/"), Credential = builder.Configuration["Providers:Service:ApiKey"] ?? "local-service-key", TimeoutSeconds = builder.Configuration.GetValue("Providers:Service:TimeoutSeconds", 10), Concurrency = builder.Configuration.GetSection("Providers:Service:Concurrency").Get<ProviderConcurrencyOptions>() ?? new ProviderConcurrencyOptions() },
    builder.Configuration.GetSection("Caching").Get<ProviderCacheOptions>() ?? new ProviderCacheOptions(),
    builder.Configuration["Caching:RedisConnection"] ?? "localhost:6379,abortConnect=false");

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi();
app.MapPrometheusScrapingEndpoint("/metrics");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

if (app.Environment.IsDevelopment())
    app.MapGet("/api/v1/auth/demo-token", () => Results.Ok(new { accessToken = JwtAuthentication.CreateDevelopmentToken(jwt), tokenType = "Bearer", expiresIn = 1800 })).AllowAnonymous().ExcludeFromDescription();

app.MapGet("/api/v1/vehicles/{vin}/documents", async (HttpContext context, string vin, IDocumentAggregator aggregator, CancellationToken cancellationToken) =>
{
    if (!IsValidVin(vin)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["vin"] = ["VIN must contain 1 to 32 letters, digits, or hyphens."] });
    if (!DealershipContext.TryCreate(context.Request, out var dealership, out var dealershipError))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["X-Dealership-Id"] = [dealershipError!] });
    Log.ForContext("EventName", "DocumentSearchStarted").Information("Document search started");
    var stopwatch = Stopwatch.StartNew();
    var result = await aggregator.SearchAsync(new DocumentLookup(vin, dealership!.DealershipId), cancellationToken);
    DocumentTelemetry.RecordSearch(result, stopwatch.Elapsed, Log.Logger);
    return result.Status == SearchStatus.Failed
        ? Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Document providers are unavailable.")
        : Results.Ok(result);
}).RequireAuthorization().WithName("GetVehicleDocuments");

app.MapGet("/api/v1/vehicles/{vin}/documents/stream", async (HttpContext context, string vin, IDocumentAggregator aggregator) =>
{
    if (!IsValidVin(vin)) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    if (!DealershipContext.TryCreate(context.Request, out var dealership, out var dealershipError))
    {
        await Results.ValidationProblem(new Dictionary<string, string[]> { ["X-Dealership-Id"] = [dealershipError!] }).ExecuteAsync(context);
        return;
    }
    DocumentTelemetry.RecordSseConnection(1);
    var timeToFirstResult = Stopwatch.StartNew();
    var firstUsefulResultRecorded = false;
    Log.ForContext("EventName", "SseConnectionOpened").Information("SSE connection opened");
    try
    {
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    await WriteEventAsync(context, "search.started", new { vin });
    var outcomes = new List<ProviderResult>();
    await foreach (var providerResult in aggregator.StreamAsync(new DocumentLookup(vin, dealership!.DealershipId), context.RequestAborted))
    {
        outcomes.Add(providerResult);
        if (!firstUsefulResultRecorded && providerResult.Status == ProviderStatus.Success)
        {
            firstUsefulResultRecorded = true;
            DocumentTelemetry.RecordTimeToFirstResult(timeToFirstResult.Elapsed);
        }
        await WriteEventAsync(context, providerResult.Status == ProviderStatus.Success ? "source.completed" : "source.failed", new { source = providerResult.Source.ToString().ToUpperInvariant(), status = providerResult.Status.ToString().ToUpperInvariant(), documents = providerResult.Documents });
    }
    var result = DocumentAggregator.BuildResult(vin, outcomes);
    DocumentTelemetry.RecordSearch(result, outcomes.Aggregate(TimeSpan.Zero, (maximum, item) => item.Duration > maximum ? item.Duration : maximum), Log.Logger);
    await WriteEventAsync(context, "search.completed", new { status = result.Status.ToString().ToUpperInvariant(), totalCount = result.TotalCount, sources = result.Sources });
    }
    finally
    {
        DocumentTelemetry.RecordSseConnection(-1);
        Log.ForContext("EventName", "SseConnectionClosed").Information("SSE connection closed");
    }
}).RequireAuthorization().ExcludeFromDescription();

app.Run();

static bool IsValidVin(string vin) => vin.Length is > 0 and <= 32 && vin.All(character => char.IsLetterOrDigit(character) || character == '-');
static async Task WriteEventAsync(HttpContext context, string name, object payload)
{
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    jsonOptions.Converters.Add(new JsonStringEnumConverter());
    await context.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, jsonOptions)}\n\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
}

public partial class Program;

public sealed class TraceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.RemovePropertyIfPresent("RequestPath");
        logEvent.RemovePropertyIfPresent("RequestId");
        logEvent.RemovePropertyIfPresent("ConnectionId");
        logEvent.RemovePropertyIfPresent("Scope");
        var activity = Activity.Current;
        if (activity is null) return;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SpanId", activity.SpanId.ToString()));
    }
}
