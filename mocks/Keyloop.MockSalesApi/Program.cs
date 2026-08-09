var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var token = builder.Configuration["Sales:BearerToken"] ?? "local-sales-token";

app.MapGet("/api/v1/vehicles/{vin}/documents", async (string vin, int page, int pageSize, HttpRequest request, CancellationToken cancellationToken) =>
{
	if (request.Headers.Authorization != $"Bearer {token}") return Results.Unauthorized();
	if (!TryGetDealershipId(request, out var dealershipId)) return Results.BadRequest();
	var scenario = Scenario(vin, request);
	var failure = await ApplyScenarioAsync(scenario, cancellationToken);
	if (failure is not null) return failure;
	if (!SalesDocumentCount(vin, out var count)) return Results.NotFound();
	var all = scenario == "empty" ? [] : CreateSalesDocuments(vin, count, dealershipId);
	var items = all.Skip((Math.Max(page, 1) - 1) * Math.Max(pageSize, 1)).Take(Math.Max(pageSize, 1)).ToArray();
	return Results.Ok(new SalesPage(items, page, pageSize, all.Length, page * pageSize < all.Length));
});

app.Run();

static string Scenario(string vin, HttpRequest request) => request.Query["scenario"].FirstOrDefault()?.ToLowerInvariant() ?? vin.ToUpperInvariant() switch
{
	"EMPTY-VEHICLE" => "empty",
	"SALES-DOWN" => "unavailable",
	"SALES-RATE-LIMITED" => "rate-limited",
	"SALES-AUTH-FAIL" => "authentication-failed",
	"SALES-TIMEOUT" => "timeout",
	"SLOW-FLEET-001" => "slow",
	_ => "normal"
};
static async Task<IResult?> ApplyScenarioAsync(string scenario, CancellationToken cancellationToken)
{
	if (scenario == "slow") await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
	if (scenario == "timeout") await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
	return scenario switch { "authentication-failed" => Results.Unauthorized(), "rate-limited" => Results.StatusCode(429), "unavailable" => Results.StatusCode(503), _ => null };
}
static bool SalesDocumentCount(string vin, out int count)
{
	count = vin.ToUpperInvariant() switch
	{
		"COMMERCIAL-001" or "SLOW-FLEET-001" or "SLOW-SERVICE-001" or "SSE-DEMO-001" or "SERVICE-DOWN" or "SERVICE-RATE-LIMITED" or "SERVICE-AUTH-FAIL" or "SERVICE-TIMEOUT" => 30,
		"FLEET-042" => 14,
		"EMPTY-VEHICLE" => 0,
		_ => -1
	};
	return count >= 0;
}
static bool TryGetDealershipId(HttpRequest request, out int dealershipId) => int.TryParse(request.Headers["X-Dealership-Id"], out dealershipId) && dealershipId > 0;
static SalesRecord[] CreateSalesDocuments(string vin, int count, int dealershipId) => Enumerable.Range(1, count).Select(index =>
{
	var (title, type) = (index % 5) switch { 0 => ("Vehicle Handover Checklist", "DELIVERY"), 1 => ("Purchase Agreement", "PURCHASE_AGREEMENT"), 2 => ("Finance Disclosure", "FINANCE"), 3 => ("Trade-In Valuation", "TRADE_IN"), _ => ("Warranty Registration", "WARRANTY") };
	return new SalesRecord($"SAL-{dealershipId}-{vin[..Math.Min(4, vin.Length)].ToUpperInvariant()}-{index:D3}", $"{title} ({dealershipId})", type, new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero).AddDays(index * 2));
}).ToArray();
record SalesPage(IReadOnlyList<SalesRecord> Items, int Page, int PageSize, int TotalCount, bool HasNextPage);
record SalesRecord(string DealDocumentId, string DocumentName, string DocumentCategory, DateTimeOffset CreatedAt);
