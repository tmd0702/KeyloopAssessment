var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var key = builder.Configuration["Service:ApiKey"] ?? "local-service-key";

app.MapGet("/api/v2/documents", async (string vehicleVin, int limit, string? cursor, HttpRequest request, CancellationToken cancellationToken) =>
{
	if (request.Headers["X-API-Key"] != key) return Results.Unauthorized();
	if (!TryGetDealershipId(request, out var dealershipId)) return Results.BadRequest();
	var scenario = Scenario(vehicleVin, request);
	if (scenario == "slow") await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
	if (scenario == "slow-service") await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
	if (scenario == "sse-demo") await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
	if (scenario == "timeout") await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
	if (scenario == "rate-limited") return Results.StatusCode(429);
	if (scenario == "unavailable") return Results.StatusCode(503);
	if (scenario == "authentication-failed") return Results.Unauthorized();
	if (!ServiceDocumentCount(vehicleVin, out var count)) return Results.NotFound();
	var all = scenario == "empty" ? [] : CreateServiceDocuments(vehicleVin, count, dealershipId);
	var offset = int.TryParse(cursor, out var parsed) ? parsed : 0;
	var page = all.Skip(offset).Take(Math.Max(limit, 1)).ToArray();
	var next = offset + page.Length < all.Length ? (offset + page.Length).ToString() : null;
	return Results.Ok(new ServicePage(page, next));
});

app.Run();

static string Scenario(string vin, HttpRequest request) => request.Query["scenario"].FirstOrDefault()?.ToLowerInvariant() ?? vin.ToUpperInvariant() switch
{
	"EMPTY-VEHICLE" => "empty",
	"SERVICE-DOWN" => "unavailable",
	"SERVICE-RATE-LIMITED" => "rate-limited",
	"SERVICE-AUTH-FAIL" => "authentication-failed",
	"SERVICE-TIMEOUT" => "timeout",
	"SLOW-FLEET-001" => "slow",
	"SLOW-SERVICE-001" => "slow-service",
	"SSE-DEMO-001" => "sse-demo",
	_ => "normal"
};
static bool ServiceDocumentCount(string vin, out int count)
{
	count = vin.ToUpperInvariant() switch
	{
		"COMMERCIAL-001" or "SLOW-FLEET-001" or "SLOW-SERVICE-001" or "SSE-DEMO-001" or "SALES-DOWN" or "SALES-RATE-LIMITED" or "SALES-AUTH-FAIL" or "SALES-TIMEOUT" => 90,
		"FLEET-042" => 37,
		"EMPTY-VEHICLE" => 0,
		_ => -1
	};
	return count >= 0;
}
static bool TryGetDealershipId(HttpRequest request, out int dealershipId) => int.TryParse(request.Headers["X-Dealership-Id"], out dealershipId) && dealershipId > 0;
static ServiceRecord[] CreateServiceDocuments(string vin, int count, int dealershipId) => Enumerable.Range(1, count).Select(index =>
{
	var (description, type) = (index % 6) switch { 0 => ("Annual Safety Inspection", "INSPECTION"), 1 => ("Service Invoice", "INVOICE"), 2 => ("Brake System Report", "REPAIR"), 3 => ("Tyre Replacement Record", "MAINTENANCE"), 4 => ("Roadside Assistance Case", "ASSISTANCE"), _ => ("Scheduled Maintenance", "MAINTENANCE") };
	return new ServiceRecord($"SRV-{dealershipId}-{vin[..Math.Min(4, vin.Length)].ToUpperInvariant()}-{index:D3}", $"{description} ({dealershipId})", type, new DateTimeOffset(2026, 3, 1, 15, 0, 0, TimeSpan.Zero).AddDays(index * 2));
}).ToArray();
record ServicePage(IReadOnlyList<ServiceRecord> Records, string? NextCursor);
record ServiceRecord(string RecordId, string Description, string RecordType, DateTimeOffset DocumentDate);
