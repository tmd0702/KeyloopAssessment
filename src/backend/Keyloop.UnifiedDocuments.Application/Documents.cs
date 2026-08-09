using System.Diagnostics;

namespace Keyloop.UnifiedDocuments.Application;

public static class UnifiedDocumentTelemetry
{
    public const string ActivitySourceName = "Keyloop.UnifiedDocuments";
    public static readonly ActivitySource Activities = new(ActivitySourceName);
}

public enum DocumentSource
{
    Sales,
    Service
}

public enum ProviderStatus
{
    Success,
    Timeout,
    Overloaded,
    Unavailable,
    RateLimited,
    AuthenticationFailed,
    InvalidResponse
}

public enum SearchStatus
{
    Complete,
    Partial,
    Failed
}

public sealed record Document(
    string Id,
    string ExternalId,
    string Title,
    string Type,
    DateTimeOffset DocumentDate,
    DocumentSource Source);

public sealed record DocumentLookup
{
    public DocumentLookup(string vin, int dealershipId)
    {
        if (string.IsNullOrWhiteSpace(vin)) throw new ArgumentException("VIN is required.", nameof(vin));
        if (dealershipId <= 0) throw new ArgumentOutOfRangeException(nameof(dealershipId), "DealershipId must be positive.");
        Vin = vin.Trim().ToUpperInvariant();
        DealershipId = dealershipId;
    }

    public string Vin { get; }
    public int DealershipId { get; }
}

public sealed record ProviderResult(
    DocumentSource Source,
    ProviderStatus Status,
    IReadOnlyList<Document> Documents,
    TimeSpan Duration)
{
    public static ProviderResult Success(DocumentSource source, IReadOnlyList<Document> documents, TimeSpan duration) =>
        new(source, ProviderStatus.Success, documents, duration);

    public static ProviderResult Failure(DocumentSource source, ProviderStatus status, TimeSpan duration) =>
        new(source, status, [], duration);
}

public sealed record SourceOutcome(
    DocumentSource Source,
    ProviderStatus Status,
    int DocumentCount,
    long DurationMs);

public sealed record DocumentSearchResult(
    string Vin,
    SearchStatus Status,
    IReadOnlyList<Document> Documents,
    IReadOnlyList<SourceOutcome> Sources)
{
    public int TotalCount => Documents.Count;
}

public interface IDocumentProvider
{
    DocumentSource Source { get; }

    Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken);
}

public interface IDocumentAggregator
{
    Task<DocumentSearchResult> SearchAsync(DocumentLookup lookup, CancellationToken cancellationToken);
    IAsyncEnumerable<ProviderResult> StreamAsync(DocumentLookup lookup, CancellationToken cancellationToken);
}

public sealed class SearchOptions
{
    public int OverallTimeoutSeconds { get; init; } = 12;
}

public sealed class DocumentAggregator(
    IEnumerable<IDocumentProvider> providers,
    SearchOptions? options = null,
    IAuditEventPublisher? auditEvents = null) : IDocumentAggregator
{
    private readonly IReadOnlyDictionary<DocumentSource, IDocumentProvider> _providers = providers.ToDictionary(provider => provider.Source);
    private readonly SearchOptions _options = options ?? new SearchOptions();
    private readonly IAuditEventPublisher _auditEvents = auditEvents ?? new NullAuditEventPublisher();

    public async Task<DocumentSearchResult> SearchAsync(DocumentLookup lookup, CancellationToken cancellationToken)
    {
        using var activity = UnifiedDocumentTelemetry.Activities.StartActivity("document.aggregate");
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var results = new List<ProviderResult>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.OverallTimeoutSeconds));
        await foreach (var result in StreamProvidersAsync(lookup, budget.Token)) results.Add(result);
        var search = BuildResult(lookup.Vin, results);
        await CompleteSearchAsync(lookup, search, startedAtUtc, stopwatch.Elapsed, cancellationToken);
        activity?.SetTag("search.status", search.Status.ToString().ToLowerInvariant());
        activity?.SetTag("document.count", search.TotalCount);
        return search;
    }

    public async IAsyncEnumerable<ProviderResult> StreamAsync(DocumentLookup lookup, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = UnifiedDocumentTelemetry.Activities.StartActivity("document.aggregate.stream");
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var results = new List<ProviderResult>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.OverallTimeoutSeconds));
        await foreach (var result in StreamProvidersAsync(lookup, budget.Token))
        {
            results.Add(result);
            yield return result;
        }
        var search = BuildResult(lookup.Vin, results);
        await CompleteSearchAsync(lookup, search, startedAtUtc, stopwatch.Elapsed, cancellationToken);
    }

    private async IAsyncEnumerable<ProviderResult> StreamProvidersAsync(DocumentLookup lookup, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new List<Task<ProviderResult>>
        {
            GetProvider(DocumentSource.Sales).GetDocumentsAsync(lookup, cancellationToken),
            GetProvider(DocumentSource.Service).GetDocumentsAsync(lookup, cancellationToken)
        };
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            yield return await completed;
        }
    }

    public static DocumentSearchResult BuildResult(string vin, IReadOnlyList<ProviderResult> results)
    {
        var outcomes = results
            .OrderBy(result => result.Source)
            .Select(result => new SourceOutcome(result.Source, result.Status, result.Documents.Count, (long)result.Duration.TotalMilliseconds))
            .ToArray();
        var documents = results
            .Where(result => result.Status == ProviderStatus.Success)
            .SelectMany(result => result.Documents)
            .OrderByDescending(document => document.DocumentDate)
            .ThenBy(document => document.Source)
            .ThenBy(document => document.Id, StringComparer.Ordinal)
            .ToArray();
        var successCount = results.Count(result => result.Status == ProviderStatus.Success);
        var status = successCount == results.Count ? SearchStatus.Complete : successCount > 0 ? SearchStatus.Partial : SearchStatus.Failed;

        return new DocumentSearchResult(vin, status, documents, outcomes);
    }

    private IDocumentProvider GetProvider(DocumentSource source) =>
        _providers.TryGetValue(source, out var provider)
            ? provider
            : throw new InvalidOperationException($"No provider registered for {source}.");

    private async Task CompleteSearchAsync(DocumentLookup lookup, DocumentSearchResult result, DateTimeOffset startedAtUtc, TimeSpan duration, CancellationToken cancellationToken)
    {
        var searchId = Guid.NewGuid();
        var completedAtUtc = startedAtUtc.Add(duration);
        var sales = result.Sources.Single(source => source.Source == DocumentSource.Sales);
        var service = result.Sources.Single(source => source.Source == DocumentSource.Service);
        var auditEvent = new DocumentSearchAuditEvent(Guid.NewGuid(), searchId, completedAtUtc, lookup.DealershipId, lookup.Vin, result.Status, result.TotalCount, sales.Status, sales.DocumentCount, service.Status, service.DocumentCount, (long)duration.TotalMilliseconds, Activity.Current?.TraceId.ToString());
        try
        {
            await _auditEvents.PublishAsync(auditEvent, cancellationToken);
            SearchOperationsTelemetry.RecordAuditPublish(true);
        }
        catch
        {
            SearchOperationsTelemetry.RecordAuditPublish(false);
        }
    }
}