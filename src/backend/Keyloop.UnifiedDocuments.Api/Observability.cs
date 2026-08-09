using System.Diagnostics.Metrics;
using System.Diagnostics;
using Keyloop.UnifiedDocuments.Application;
using Serilog;

namespace Keyloop.UnifiedDocuments.Api;

public static class DocumentTelemetry
{
    public const string MeterName = "Keyloop.UnifiedDocuments";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> SearchTotal = Meter.CreateCounter<long>("document_search_requests");
    private static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>("document_search_duration", "s");
    private static readonly Counter<long> PartialTotal = Meter.CreateCounter<long>("document_search_partial_total");
    private static readonly Counter<long> ProviderTotal = Meter.CreateCounter<long>("provider_requests");
    private static readonly Histogram<double> ProviderDuration = Meter.CreateHistogram<double>("provider_request_duration", "s");
    private static readonly Counter<long> ProviderFailureTotal = Meter.CreateCounter<long>("provider_failures");
    private static readonly Counter<long> ProviderTimeoutTotal = Meter.CreateCounter<long>("provider_timeouts");
    private static readonly UpDownCounter<long> ActiveSseConnections = Meter.CreateUpDownCounter<long>("sse_active_connections");
    private static readonly Histogram<double> TimeToFirstResult = Meter.CreateHistogram<double>("document_search_time_to_first_result", "s");

    public static void RecordSseConnection(long change) => ActiveSseConnections.Add(change);
    public static void RecordTimeToFirstResult(TimeSpan duration) => TimeToFirstResult.Record(duration.TotalSeconds);

    public static void RecordSearch(DocumentSearchResult result, TimeSpan duration, Serilog.ILogger logger)
    {
        var searchTags = new TagList { { "status", result.Status.ToString().ToLowerInvariant() } };
        SearchTotal.Add(1, searchTags);
        SearchDuration.Record(duration.TotalSeconds, searchTags);
        if (result.Status == SearchStatus.Partial) PartialTotal.Add(1);

        foreach (var source in result.Sources)
        {
            var providerTags = new TagList { { "provider", source.Source.ToString().ToLowerInvariant() }, { "status", source.Status.ToString().ToLowerInvariant() } };
            ProviderTotal.Add(1, providerTags);
            ProviderDuration.Record(source.DurationMs / 1000d, providerTags);
            if (source.Status != ProviderStatus.Success) ProviderFailureTotal.Add(1, new TagList { { "provider", source.Source.ToString().ToLowerInvariant() }, { "failure_type", source.Status.ToString().ToLowerInvariant() } });
            if (source.Status == ProviderStatus.Timeout) ProviderTimeoutTotal.Add(1, new TagList { { "provider", source.Source.ToString().ToLowerInvariant() } });

        }

        var sales = result.Sources.SingleOrDefault(source => source.Source == DocumentSource.Sales);
        var service = result.Sources.SingleOrDefault(source => source.Source == DocumentSource.Service);
        logger.Information(
            "Document search completed {EventName} {SearchStatus} {DurationMs} {DocumentCount} {SalesStatus} {ServiceStatus}",
            "DocumentSearchCompleted",
            result.Status.ToString().ToUpperInvariant(),
            duration.TotalMilliseconds,
            result.TotalCount,
            sales?.Status.ToString().ToUpperInvariant(),
            service?.Status.ToString().ToUpperInvariant());
    }
}