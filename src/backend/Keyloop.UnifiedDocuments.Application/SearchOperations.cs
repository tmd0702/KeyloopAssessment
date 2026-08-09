using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Keyloop.UnifiedDocuments.Application;

public sealed record DocumentSearchAuditEvent(
    Guid EventId,
    Guid SearchId,
    DateTimeOffset OccurredAtUtc,
    int DealershipId,
    string Vin,
    SearchStatus SearchStatus,
    int TotalDocumentCount,
    ProviderStatus SalesStatus,
    int SalesDocumentCount,
    ProviderStatus ServiceStatus,
    int ServiceDocumentCount,
    long DurationMs,
    string? TraceId);

public interface IAuditEventPublisher
{
    Task PublishAsync(DocumentSearchAuditEvent auditEvent, CancellationToken cancellationToken);
}

public sealed class NullAuditEventPublisher : IAuditEventPublisher
{
    public Task PublishAsync(DocumentSearchAuditEvent auditEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class SearchOperationsTelemetry
{
    public const string MeterName = "Keyloop.UnifiedDocuments.SearchOperations";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> AuditPublishTotal = Meter.CreateCounter<long>("audit_event_publish_total");
    private static readonly Counter<long> AuditPublishFailures = Meter.CreateCounter<long>("audit_event_publish_failures_total");

    public static void RecordAuditPublish(bool succeeded)
    {
        var tags = new TagList { { "result", succeeded ? "success" : "failure" } };
        AuditPublishTotal.Add(1, tags);
        if (!succeeded) AuditPublishFailures.Add(1);
    }
}