using FluentAssertions;
using Keyloop.UnifiedDocuments.Application;

namespace Keyloop.UnifiedDocuments.UnitTests;

public sealed class DocumentAggregatorTests
{
    [Fact]
    public async Task SearchAsync_when_both_sources_succeed_returns_complete_merged_result()
    {
        var salesDocument = CreateDocument(DocumentSource.Sales, "DOC-1", new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var serviceDocument = CreateDocument(DocumentSource.Service, "DOC-1", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var aggregator = CreateAggregator(ProviderResult.Success(DocumentSource.Sales, [salesDocument], TimeSpan.FromMilliseconds(10)), ProviderResult.Success(DocumentSource.Service, [serviceDocument], TimeSpan.FromMilliseconds(20)));

        var result = await aggregator.SearchAsync(new DocumentLookup("1HGCM82633A004352", 42), CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Complete);
        result.Documents.Select(document => document.Id).Should().Equal("SERVICE:DOC-1", "SALES:DOC-1");
    }

    [Fact]
    public async Task SearchAsync_when_one_source_fails_returns_partial_and_retains_successful_documents()
    {
        var salesDocument = CreateDocument(DocumentSource.Sales, "DOC-1", DateTimeOffset.UtcNow);
        var aggregator = CreateAggregator(ProviderResult.Success(DocumentSource.Sales, [salesDocument], TimeSpan.Zero), ProviderResult.Failure(DocumentSource.Service, ProviderStatus.Unavailable, TimeSpan.Zero));

        var result = await aggregator.SearchAsync(new DocumentLookup("1HGCM82633A004352", 42), CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Partial);
        result.Documents.Should().ContainSingle().Which.Source.Should().Be(DocumentSource.Sales);
        result.Sources.Single(outcome => outcome.Source == DocumentSource.Service).Status.Should().Be(ProviderStatus.Unavailable);
    }

    [Fact]
    public async Task SearchAsync_when_both_sources_fail_returns_failed_without_documents()
    {
        var aggregator = CreateAggregator(ProviderResult.Failure(DocumentSource.Sales, ProviderStatus.Timeout, TimeSpan.Zero), ProviderResult.Failure(DocumentSource.Service, ProviderStatus.RateLimited, TimeSpan.Zero));

        var result = await aggregator.SearchAsync(new DocumentLookup("1HGCM82633A004352", 42), CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Failed);
        result.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_propagates_cancellation_to_both_providers()
    {
        using var cancellation = new CancellationTokenSource();
        var sales = new CapturingProvider(DocumentSource.Sales);
        var service = new CapturingProvider(DocumentSource.Service);
        var aggregator = new DocumentAggregator([sales, service]);

        await aggregator.SearchAsync(new DocumentLookup("1HGCM82633A004352", 42), cancellation.Token);

        sales.ReceivedToken.CanBeCanceled.Should().BeTrue();
        service.ReceivedToken.CanBeCanceled.Should().BeTrue();
    }

    private static DocumentAggregator CreateAggregator(ProviderResult sales, ProviderResult service) =>
        new([new StubProvider(DocumentSource.Sales, sales), new StubProvider(DocumentSource.Service, service)]);

    private static Document CreateDocument(DocumentSource source, string externalId, DateTimeOffset date) =>
        new($"{source.ToString().ToUpperInvariant()}:{externalId}", externalId, "Document", "TYPE", date, source);

    private sealed class StubProvider(DocumentSource source, ProviderResult result) : IDocumentProvider
    {
        public DocumentSource Source { get; } = source;
        public Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class CapturingProvider(DocumentSource source) : IDocumentProvider
    {
        public DocumentSource Source { get; } = source;
        public CancellationToken ReceivedToken { get; private set; }

        public Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            return Task.FromResult(ProviderResult.Success(Source, [], TimeSpan.Zero));
        }
    }
}