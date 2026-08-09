using FluentAssertions;
using Keyloop.UnifiedDocuments.Application;

namespace Keyloop.UnifiedDocuments.UnitTests;

public sealed class SearchOperationsTests
{
    [Fact]
    public async Task Successful_search_publishes_one_metadata_only_audit_event()
    {
        var audit = new CapturingAudit();
        var aggregator = CreateAggregator(audit);

        var result = await aggregator.SearchAsync(new DocumentLookup("COMMERCIAL-001", 42), CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Complete);
        audit.Items.Should().ContainSingle();
        audit.Items[0].Should().BeEquivalentTo(new
        {
            DealershipId = 42,
            Vin = "COMMERCIAL-001",
            SearchStatus = SearchStatus.Complete,
            TotalDocumentCount = 2,
            SalesStatus = ProviderStatus.Success,
            SalesDocumentCount = 1,
            ServiceStatus = ProviderStatus.Success,
            ServiceDocumentCount = 1
        });
        typeof(DocumentSearchAuditEvent).GetProperties().Select(property => property.Name).Should().NotContain(["Documents", "Credential", "BearerToken", "ApiKey", "RedisKey", "LockToken"]);
    }

    [Fact]
    public async Task Disabled_audit_publisher_does_not_contact_an_external_service()
    {
        var publisher = new NullAuditEventPublisher();
        var auditEvent = new DocumentSearchAuditEvent(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, 42, "COMMERCIAL-001", SearchStatus.Complete, 2, ProviderStatus.Success, 1, ProviderStatus.Success, 1, 10, null);

        var publish = () => publisher.PublishAsync(auditEvent, CancellationToken.None);

        await publish.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Audit_failure_does_not_discard_a_complete_result()
    {
        var aggregator = CreateAggregator(new ThrowingAudit());

        var result = await aggregator.SearchAsync(new DocumentLookup("COMMERCIAL-001", 42), CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Complete);
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Audit_failure_does_not_change_a_partial_result()
    {
        var sales = new StubProvider(DocumentSource.Sales, ProviderResult.Success(DocumentSource.Sales, [CreateDocument(DocumentSource.Sales)], TimeSpan.Zero));
        var service = new StubProvider(DocumentSource.Service, ProviderResult.Failure(DocumentSource.Service, ProviderStatus.Unavailable, TimeSpan.Zero));
        var aggregator = new DocumentAggregator([sales, service], null, new ThrowingAudit());

        var result = await aggregator.SearchAsync(new DocumentLookup("COMMERCIAL-001", 42), CancellationToken.None);

        result.Status.Should().Be(SearchStatus.Partial);
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Stream_yields_provider_results_before_audit_publication_completes()
    {
        var blockingAudit = new BlockingAudit();
        var aggregator = CreateAggregator(blockingAudit);
        await using var results = aggregator.StreamAsync(new DocumentLookup("SSE-DEMO-001", 42), CancellationToken.None).GetAsyncEnumerator();

        (await results.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        (await results.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();

        blockingAudit.PublishStarted.Should().BeFalse();
    }

    private static DocumentAggregator CreateAggregator(IAuditEventPublisher audit) =>
        new(
            [
                new StubProvider(DocumentSource.Sales, ProviderResult.Success(DocumentSource.Sales, [CreateDocument(DocumentSource.Sales)], TimeSpan.Zero)),
                new StubProvider(DocumentSource.Service, ProviderResult.Success(DocumentSource.Service, [CreateDocument(DocumentSource.Service)], TimeSpan.Zero))
            ],
            null,
            audit);

    private static Document CreateDocument(DocumentSource source) =>
        new($"{source}:1", "1", "A provider document", "TYPE", DateTimeOffset.UtcNow, source);

    private sealed class StubProvider(DocumentSource source, ProviderResult result) : IDocumentProvider
    {
        public DocumentSource Source { get; } = source;
        public Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class CapturingAudit : IAuditEventPublisher
    {
        public List<DocumentSearchAuditEvent> Items { get; } = [];
        public Task PublishAsync(DocumentSearchAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Items.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAudit : IAuditEventPublisher
    {
        public Task PublishAsync(DocumentSearchAuditEvent auditEvent, CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("EventHub unavailable"));
    }

    private sealed class BlockingAudit : IAuditEventPublisher
    {
        public bool PublishStarted { get; private set; }
        public Task PublishAsync(DocumentSearchAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            PublishStarted = true;
            return new TaskCompletionSource().Task;
        }
    }
}