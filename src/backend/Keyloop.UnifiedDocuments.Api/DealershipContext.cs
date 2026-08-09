namespace Keyloop.UnifiedDocuments.Api;

public interface IDealershipContext
{
    int DealershipId { get; }
}

public sealed record DealershipContext(int DealershipId) : IDealershipContext
{
    public static bool TryCreate(HttpRequest request, out DealershipContext? context, out string? error)
    {
        context = null;
        error = null;
        if (!request.Headers.TryGetValue("X-Dealership-Id", out var value) || !int.TryParse(value, out var dealershipId) || dealershipId <= 0)
        {
            error = "X-Dealership-Id must be a positive integer.";
            return false;
        }
        context = new DealershipContext(dealershipId);
        return true;
    }
}