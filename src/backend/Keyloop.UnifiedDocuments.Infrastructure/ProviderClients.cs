using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Keyloop.UnifiedDocuments.Application;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;

namespace Keyloop.UnifiedDocuments.Infrastructure;

public sealed class ProviderOptions
{
    public required Uri BaseAddress { get; init; }
    public required string Credential { get; init; }
    public int TimeoutSeconds { get; init; } = 8;
    public ProviderConcurrencyOptions Concurrency { get; init; } = new();
}

public sealed class ProviderConcurrencyOptions
{
    public int PermitLimit { get; init; } = 20;
    public int QueueLimit { get; init; } = 20;
    public int QueueTimeoutMilliseconds { get; init; } = 500;
}

public sealed record ProviderRuntimeOptions(ProviderOptions Sales, ProviderOptions Service);

public sealed class ProviderConcurrencyLimiter : IDisposable
{
    private static readonly Meter Meter = new(ProviderResultCache.MeterName);
    private static readonly Histogram<double> WaitDuration = Meter.CreateHistogram<double>("provider_concurrency_wait_duration", "s");
    private static readonly Counter<long> Acquisitions = Meter.CreateCounter<long>("provider_concurrency_acquisitions_total");
    private static readonly Counter<long> Rejections = Meter.CreateCounter<long>("provider_concurrency_rejections_total");
    private readonly ConcurrencyLimiter _limiter;
    private readonly ProviderConcurrencyOptions _options;
    private readonly string _provider;

    public ProviderConcurrencyLimiter(DocumentSource source, ProviderConcurrencyOptions options)
    {
        _provider = source.ToString().ToLowerInvariant();
        _options = options;
        _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = options.PermitLimit, QueueLimit = options.QueueLimit, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });
    }

    public string Provider => _provider;

    public async Task<RateLimitLease?> AcquireAsync(CancellationToken cancellationToken)
    {
        var wait = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.QueueTimeoutMilliseconds));
        try
        {
            var lease = await _limiter.AcquireAsync(1, timeout.Token);
            WaitDuration.Record(wait.Elapsed.TotalSeconds, new TagList { { "provider", _provider }, { "result", lease.IsAcquired ? "acquired" : "rejected" } });
            if (lease.IsAcquired) Acquisitions.Add(1, new TagList { { "provider", _provider }, { "result", "acquired" } });
            else Rejections.Add(1, new TagList { { "provider", _provider }, { "result", "rejected" } });
            return lease;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Rejections.Add(1, new TagList { { "provider", _provider }, { "result", "rejected" } });
            return null;
        }
    }

    public void Dispose() => _limiter.Dispose();
}

public sealed class SalesDocumentProvider(HttpClient client, ProviderRuntimeOptions runtime, IEnumerable<ProviderConcurrencyLimiter> limiters, ILogger<SalesDocumentProvider> logger) : IDocumentProvider
{
    public DocumentSource Source => DocumentSource.Sales;

    public async Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken)
    {
        using var activity = UnifiedDocumentTelemetry.Activities.StartActivity("provider.sales");
        activity?.SetTag("provider", "sales");
        logger.LogInformation("Sales provider request started {EventName} {Provider}", "ProviderRequestStarted", "SALES");
        var stopwatch = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(runtime.Sales.TimeoutSeconds));
        try
        {
            var limiter = limiters.Single(item => item.Provider == "sales");
            using var lease = await limiter.AcquireAsync(budget.Token);
            if (lease is null || !lease.IsAcquired)
            {
                logger.LogWarning("Provider concurrency rejected {EventName} {Provider}", "ProviderConcurrencyRejected", "SALES");
                return Complete(ProviderResult.Failure(Source, ProviderStatus.Overloaded, stopwatch.Elapsed));
            }
            using var concurrencyActivity = UnifiedDocumentTelemetry.Activities.StartActivity("provider.concurrency_wait");
            concurrencyActivity?.SetTag("provider", "sales");
            var documents = new List<Document>();
            for (var page = 1; ; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/vehicles/{Uri.EscapeDataString(lookup.Vin)}/documents?page={page}&pageSize=50");
                request.Headers.Add("X-Dealership-Id", lookup.DealershipId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var response = await client.SendAsync(request, budget.Token);
                if (!response.IsSuccessStatusCode) return Complete(ProviderResult.Failure(Source, MapStatus(response.StatusCode), stopwatch.Elapsed));
                var payload = await response.Content.ReadFromJsonAsync<SalesPage>(cancellationToken: budget.Token);
                if (payload is null || payload.Items is null) return Complete(ProviderResult.Failure(Source, ProviderStatus.InvalidResponse, stopwatch.Elapsed));
                documents.AddRange(payload.Items.Select(item => DocumentNormalizers.FromSales(item.DealDocumentId, item.DocumentName, item.DocumentCategory, item.CreatedAt)));
                if (!payload.HasNextPage) return Complete(ProviderResult.Success(Source, documents, stopwatch.Elapsed));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { logger.LogWarning("Provider budget exceeded {EventName} {Provider}", "ProviderBudgetExceeded", "SALES"); return Complete(ProviderResult.Failure(Source, ProviderStatus.Timeout, stopwatch.Elapsed)); }
        catch (HttpRequestException) { return Complete(ProviderResult.Failure(Source, ProviderStatus.Unavailable, stopwatch.Elapsed)); }
        catch (Exception) { return Complete(ProviderResult.Failure(Source, ProviderStatus.InvalidResponse, stopwatch.Elapsed)); }

        ProviderResult Complete(ProviderResult result)
        {
            activity?.SetTag("provider.status", result.Status.ToString().ToLowerInvariant());
            activity?.SetTag("document.count", result.Documents.Count);
            if (result.Status != ProviderStatus.Success) activity?.SetStatus(ActivityStatusCode.Error, result.Status.ToString());
            if (result.Status == ProviderStatus.Success)
                logger.LogInformation("Sales provider request completed {EventName} {Provider} {DurationMs} {DocumentCount}", "ProviderRequestCompleted", "SALES", result.Duration.TotalMilliseconds, result.Documents.Count);
            else
                logger.LogWarning("Sales provider request failed {EventName} {Provider} {DurationMs} {FailureType}", "ProviderRequestFailed", "SALES", result.Duration.TotalMilliseconds, result.Status.ToString().ToUpperInvariant());
            return result;
        }
    }

    private static ProviderStatus MapStatus(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderStatus.AuthenticationFailed,
        HttpStatusCode.TooManyRequests => ProviderStatus.RateLimited,
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => ProviderStatus.Unavailable,
        _ => ProviderStatus.InvalidResponse
    };
}

public sealed class ServiceDocumentProvider(HttpClient client, ProviderRuntimeOptions runtime, IEnumerable<ProviderConcurrencyLimiter> limiters, ILogger<ServiceDocumentProvider> logger) : IDocumentProvider
{
    public DocumentSource Source => DocumentSource.Service;

    public async Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken)
    {
        using var activity = UnifiedDocumentTelemetry.Activities.StartActivity("provider.service");
        activity?.SetTag("provider", "service");
        logger.LogInformation("Service provider request started {EventName} {Provider}", "ProviderRequestStarted", "SERVICE");
        var stopwatch = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(runtime.Service.TimeoutSeconds));
        try
        {
            var limiter = limiters.Single(item => item.Provider == "service");
            using var lease = await limiter.AcquireAsync(budget.Token);
            if (lease is null || !lease.IsAcquired)
            {
                logger.LogWarning("Provider concurrency rejected {EventName} {Provider}", "ProviderConcurrencyRejected", "SERVICE");
                return Complete(ProviderResult.Failure(Source, ProviderStatus.Overloaded, stopwatch.Elapsed));
            }
            using var concurrencyActivity = UnifiedDocumentTelemetry.Activities.StartActivity("provider.concurrency_wait");
            concurrencyActivity?.SetTag("provider", "service");
            var documents = new List<Document>();
            string? cursor = null;
            do
            {
                var path = $"api/v2/documents?vehicleVin={Uri.EscapeDataString(lookup.Vin)}&limit=50" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Add("X-Dealership-Id", lookup.DealershipId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var response = await client.SendAsync(request, budget.Token);
                if (!response.IsSuccessStatusCode) return Complete(ProviderResult.Failure(Source, MapStatus(response.StatusCode), stopwatch.Elapsed));
                var payload = await response.Content.ReadFromJsonAsync<ServicePage>(cancellationToken: budget.Token);
                if (payload?.Records is null) return Complete(ProviderResult.Failure(Source, ProviderStatus.InvalidResponse, stopwatch.Elapsed));
                documents.AddRange(payload.Records.Select(item => DocumentNormalizers.FromService(item.RecordId, item.Description, item.RecordType, item.DocumentDate)));
                cursor = payload.NextCursor;
            } while (cursor is not null);
            return Complete(ProviderResult.Success(Source, documents, stopwatch.Elapsed));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { logger.LogWarning("Provider budget exceeded {EventName} {Provider}", "ProviderBudgetExceeded", "SERVICE"); return Complete(ProviderResult.Failure(Source, ProviderStatus.Timeout, stopwatch.Elapsed)); }
        catch (HttpRequestException) { return Complete(ProviderResult.Failure(Source, ProviderStatus.Unavailable, stopwatch.Elapsed)); }
        catch (Exception) { return Complete(ProviderResult.Failure(Source, ProviderStatus.InvalidResponse, stopwatch.Elapsed)); }

        ProviderResult Complete(ProviderResult result)
        {
            activity?.SetTag("provider.status", result.Status.ToString().ToLowerInvariant());
            activity?.SetTag("document.count", result.Documents.Count);
            if (result.Status != ProviderStatus.Success) activity?.SetStatus(ActivityStatusCode.Error, result.Status.ToString());
            if (result.Status == ProviderStatus.Success)
                logger.LogInformation("Service provider request completed {EventName} {Provider} {DurationMs} {DocumentCount}", "ProviderRequestCompleted", "SERVICE", result.Duration.TotalMilliseconds, result.Documents.Count);
            else
                logger.LogWarning("Service provider request failed {EventName} {Provider} {DurationMs} {FailureType}", "ProviderRequestFailed", "SERVICE", result.Duration.TotalMilliseconds, result.Status.ToString().ToUpperInvariant());
            return result;
        }
    }

    private static ProviderStatus MapStatus(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderStatus.AuthenticationFailed,
        HttpStatusCode.TooManyRequests => ProviderStatus.RateLimited,
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => ProviderStatus.Unavailable,
        _ => ProviderStatus.InvalidResponse
    };
}

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentProviders(this IServiceCollection services, ProviderOptions sales, ProviderOptions service, ProviderCacheOptions cacheOptions, string redisConnection)
    {
        services.AddHttpClient<SalesDocumentProvider>(client =>
        {
            client.BaseAddress = sales.BaseAddress;
            client.DefaultRequestHeaders.Authorization = new("Bearer", sales.Credential);
        }).AddStandardResilienceHandler(options => { options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(Math.Min(4, sales.TimeoutSeconds)); options.Retry.MaxRetryAttempts = 1; });
        services.AddHttpClient<ServiceDocumentProvider>(client =>
        {
            client.BaseAddress = service.BaseAddress;
            client.DefaultRequestHeaders.Add("X-API-Key", service.Credential);
        }).AddStandardResilienceHandler(options => { options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(Math.Min(6, service.TimeoutSeconds)); options.Retry.MaxRetryAttempts = 1; });
        services.AddStackExchangeRedisCache(options => options.Configuration = redisConnection);
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
        services.AddSingleton(cacheOptions);
        services.AddSingleton(new ProviderRuntimeOptions(sales, service));
        services.AddSingleton<ProviderConcurrencyLimiter>(_ => new ProviderConcurrencyLimiter(DocumentSource.Sales, sales.Concurrency));
        services.AddSingleton<ProviderConcurrencyLimiter>(_ => new ProviderConcurrencyLimiter(DocumentSource.Service, service.Concurrency));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new DocumentCacheKeyFactory(cacheOptions.KeyPrefix));
        services.AddSingleton<IDistributedDocumentLock, RedisDistributedDocumentLock>();
        services.AddSingleton<ProviderResultCache>();
        services.AddSingleton<IDocumentProvider>(serviceProvider => new CachedDocumentProvider(serviceProvider.GetRequiredService<SalesDocumentProvider>(), serviceProvider.GetRequiredService<ProviderResultCache>()));
        services.AddSingleton<IDocumentProvider>(serviceProvider => new CachedDocumentProvider(serviceProvider.GetRequiredService<ServiceDocumentProvider>(), serviceProvider.GetRequiredService<ProviderResultCache>()));
        return services;
    }
}

internal sealed record SalesPage(IReadOnlyList<SalesRecord>? Items, int Page, int PageSize, int TotalCount, bool HasNextPage);
internal sealed record SalesRecord(string DealDocumentId, string DocumentName, string DocumentCategory, DateTimeOffset CreatedAt);
internal sealed record ServicePage(IReadOnlyList<ServiceRecord>? Records, string? NextCursor);
internal sealed record ServiceRecord(string RecordId, string Description, string RecordType, DateTimeOffset DocumentDate);