using FluentAssertions;
using Keyloop.UnifiedDocuments.Application;

namespace Keyloop.UnifiedDocuments.UnitTests;

public sealed class CoreDocumentAggregationTests
{
    private static readonly DocumentLookup Lookup = new("1HGCM82633A004352", 42);

    [Fact]
    public async Task SearchAsync_WhenBothProvidersSucceed_ReturnsCompleteAggregatedResult()
    {
        var salesDocuments = new[] { Document(DocumentSource.Sales, "100", 1), Document(DocumentSource.Sales, "101", 3) };
        var serviceDocuments = new[] { Document(DocumentSource.Service, "100", 2), Document(DocumentSource.Service, "102", 4) };
        var aggregator = Aggregator(Success(DocumentSource.Sales, salesDocuments), Success(DocumentSource.Service, serviceDocuments));

        var result = await aggregator.SearchAsync(Lookup, CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Complete);
        result.TotalCount.Should().Be(4);
        result.Documents.Select(document => document.Id).Should().Equal("SERVICE:102", "SALES:101", "SERVICE:100", "SALES:100");
        result.Documents.Where(document => document.Source == DocumentSource.Sales).Should().HaveCount(2);
        result.Documents.Where(document => document.Source == DocumentSource.Service).Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_WhenServiceFails_ReturnsPartialSalesResult()
    {
        var salesDocument = Document(DocumentSource.Sales, "sales", 1);
        var result = await Aggregator(Success(DocumentSource.Sales, [salesDocument]), Failure(DocumentSource.Service, ProviderStatus.Unavailable)).SearchAsync(Lookup, CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Partial);
        result.Documents.Should().Equal(salesDocument);
        result.Sources.Single(outcome => outcome.Source == DocumentSource.Service).Status.Should().Be(ProviderStatus.Unavailable);
    }

    [Fact]
    public async Task SearchAsync_WhenSalesFails_ReturnsPartialServiceResult()
    {
        var serviceDocument = Document(DocumentSource.Service, "service", 1);
        var result = await Aggregator(Failure(DocumentSource.Sales, ProviderStatus.Timeout), Success(DocumentSource.Service, [serviceDocument])).SearchAsync(Lookup, CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Partial);
        result.Documents.Should().Equal(serviceDocument);
        result.Sources.Single(outcome => outcome.Source == DocumentSource.Sales).Status.Should().Be(ProviderStatus.Timeout);
    }

    [Fact]
    public async Task SearchAsync_WhenBothProvidersFail_ReturnsFailedResult()
    {
        var result = await Aggregator(Failure(DocumentSource.Sales, ProviderStatus.RateLimited), Failure(DocumentSource.Service, ProviderStatus.Unavailable)).SearchAsync(Lookup, CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Failed);
        result.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WhenOneProviderReturnsEmptySuccess_RemainsComplete()
    {
        var serviceDocument = Document(DocumentSource.Service, "service", 1);
        var result = await Aggregator(Success(DocumentSource.Sales, []), Success(DocumentSource.Service, [serviceDocument])).SearchAsync(Lookup, CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Complete);
        result.Documents.Should().Equal(serviceDocument);
        result.Sources.Single(outcome => outcome.Source == DocumentSource.Sales).Should().Match<SourceOutcome>(outcome => outcome.Status == ProviderStatus.Success && outcome.DocumentCount == 0);
    }

    [Fact]
    public async Task SearchAsync_WhenBothProvidersReturnEmptySuccess_ReturnsCompleteEmptyResult()
    {
        var result = await Aggregator(Success(DocumentSource.Sales, []), Success(DocumentSource.Service, [])).SearchAsync(Lookup, CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Complete);
        result.Documents.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_WhenDocumentsHaveSameDate_UsesSourceThenStableIdTieBreakers()
    {
        var sameDate = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var result = await Aggregator(
            Success(DocumentSource.Sales, [Document(DocumentSource.Sales, "B", sameDate), Document(DocumentSource.Sales, "A", sameDate)]),
            Success(DocumentSource.Service, [Document(DocumentSource.Service, "A", sameDate)])).SearchAsync(Lookup, CancellationToken.None);

        result.Documents.Select(document => document.Id).Should().Equal("SALES:A", "SALES:B", "SERVICE:A");
    }

    [Fact]
    public async Task SearchAsync_StartsSalesAndServiceProvidersConcurrently()
    {
        var sales = new ControllableProvider(DocumentSource.Sales);
        var service = new ControllableProvider(DocumentSource.Service);
        var search = new DocumentAggregator([sales, service]).SearchAsync(Lookup, CancellationToken.None);

        await Task.WhenAll(sales.Started.Task, service.Started.Task).WaitAsync(TimeSpan.FromSeconds(1));
        sales.Completed.Task.IsCompleted.Should().BeFalse();
        service.Completed.Task.IsCompleted.Should().BeFalse();
        sales.Complete(Success(DocumentSource.Sales, []));
        service.Complete(Success(DocumentSource.Service, []));

        (await search).Status.Should().Be(SearchStatus.Complete);
    }

    [Fact]
    public async Task SearchAsync_WhenSalesIsSlow_StartsServiceWithoutWaitingForSales()
    {
        var sales = new ControllableProvider(DocumentSource.Sales);
        var service = new ControllableProvider(DocumentSource.Service);
        var search = new DocumentAggregator([sales, service]).SearchAsync(Lookup, CancellationToken.None);

        await sales.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        service.Complete(Success(DocumentSource.Service, [Document(DocumentSource.Service, "fast", 1)]));
        sales.Complete(Success(DocumentSource.Sales, []));

        (await search).Status.Should().Be(SearchStatus.Complete);
    }

    [Fact]
    public async Task SearchAsync_WhenCallerCancels_PropagatesCancellationToBothProviders()
    {
        using var cancellation = new CancellationTokenSource();
        var sales = new CancellingProvider(DocumentSource.Sales);
        var service = new CancellingProvider(DocumentSource.Service);
        var search = new DocumentAggregator([sales, service]).SearchAsync(Lookup, cancellation.Token);

        await Task.WhenAll(sales.Started.Task, service.Started.Task).WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await FluentActions.Awaiting(() => search).Should().ThrowAsync<OperationCanceledException>();
        sales.ReceivedToken.IsCancellationRequested.Should().BeTrue();
        service.ReceivedToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void DocumentLookup_WhenWhitespaceAndLowercase_NormalizesVin()
    {
        var lookup = new DocumentLookup("  1hgcm82633a004352  ", 42);

        lookup.Vin.Should().Be("1HGCM82633A004352");
        lookup.DealershipId.Should().Be(42);
    }

    private static DocumentAggregator Aggregator(ProviderResult sales, ProviderResult service) =>
        new([new ResultProvider(DocumentSource.Sales, sales), new ResultProvider(DocumentSource.Service, service)]);

    private static ProviderResult Success(DocumentSource source, IReadOnlyList<Document> documents) => ProviderResult.Success(source, documents, TimeSpan.Zero);
    private static ProviderResult Failure(DocumentSource source, ProviderStatus status) => ProviderResult.Failure(source, status, TimeSpan.Zero);
    private static Document Document(DocumentSource source, string externalId, int month) => Document(source, externalId, new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero));
    private static Document Document(DocumentSource source, string externalId, DateTimeOffset date) => new($"{source.ToString().ToUpperInvariant()}:{externalId}", externalId, $"{source} document", "Invoice", date, source);

    private sealed class ResultProvider(DocumentSource source, ProviderResult result) : IDocumentProvider
    {
        public DocumentSource Source { get; } = source;
        public Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ControllableProvider(DocumentSource source) : IDocumentProvider
    {
        public DocumentSource Source { get; } = source;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProviderResult> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return Completed.Task;
        }
        public void Complete(ProviderResult result) => Completed.TrySetResult(result);
    }

    private sealed class CancellingProvider(DocumentSource source) : IDocumentProvider
    {
        public DocumentSource Source { get; } = source;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReceivedToken { get; private set; }
        public async Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-aware provider should not complete normally.");
        }
    }
}