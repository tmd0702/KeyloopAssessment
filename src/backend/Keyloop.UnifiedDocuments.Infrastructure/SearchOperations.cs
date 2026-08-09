using Keyloop.UnifiedDocuments.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Keyloop.UnifiedDocuments.Infrastructure;

public sealed class AuditOptions
{
    public bool Enabled { get; init; }
    public string? EventHubName { get; init; }
}

public sealed class MockEventHubAuditEventPublisher(AuditOptions options, ILogger<MockEventHubAuditEventPublisher> logger) : IAuditEventPublisher
{
    public Task PublishAsync(DocumentSearchAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Mock EventHub audit event published {EventName} {SearchId} {SearchStatus} {DurationMs} {DocumentCount} {EventHubName}", "AuditEventPublished", auditEvent.SearchId, auditEvent.SearchStatus, auditEvent.DurationMs, auditEvent.TotalDocumentCount, options.EventHubName ?? "document-search-audit");
        return Task.CompletedTask;
    }
}

public static class SearchOperationsServiceCollectionExtensions
{
    public static IServiceCollection AddSearchOperations(this IServiceCollection services, AuditOptions audit)
    {
        if (!audit.Enabled)
            services.AddSingleton<IAuditEventPublisher, NullAuditEventPublisher>();
        else
            services.AddSingleton<IAuditEventPublisher>(serviceProvider => new MockEventHubAuditEventPublisher(audit, serviceProvider.GetRequiredService<ILogger<MockEventHubAuditEventPublisher>>()));

        return services;
    }
}