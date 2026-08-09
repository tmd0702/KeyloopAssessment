using System.Net;
using System.Text;
using FluentAssertions;
using Keyloop.UnifiedDocuments.Application;

namespace Keyloop.UnifiedDocuments.IntegrationTests.Api;

public sealed class DocumentsStreamEndpointTests
{
    [Fact]
    public async Task Stream_WhenSalesCompletesFirst_EmitsSalesBeforeServiceCompletes()
    {
        var sales = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aggregator = new ScriptedAggregator(stream: (_, cancellationToken) => ControlledStream(sales.Task, service.Task, streamStarted, cancellationToken));
        using var factory = new UnifiedDocumentsApiFactory(aggregator);
        using var client = await factory.CreateAuthorizedClientAsync();
        using var request = StreamRequest("SSE-DEMO-001");

        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        sales.SetResult(ScriptedAggregator.Success(DocumentSource.Sales));
        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(1));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        var firstEvents = await ReadEventBlockAsync(reader, "source.completed", TimeSpan.FromSeconds(1));

        firstEvents.Should().Contain("event: search.started");
        firstEvents.Should().Contain("event: source.completed");
        firstEvents.Should().Contain("\"source\":\"SALES\"");
        service.Task.IsCompleted.Should().BeFalse();

        service.SetResult(ScriptedAggregator.Success(DocumentSource.Service));
        var finalEvents = await ReadEventBlockAsync(reader, "search.completed", TimeSpan.FromSeconds(1));
        finalEvents.Should().Contain("\"source\":\"SERVICE\"");
        finalEvents.Should().Contain("event: search.completed");
    }

    [Fact]
    public async Task Stream_WhenServiceFails_EmitsPartialCompletion()
    {
        var sales = ScriptedAggregator.Success(DocumentSource.Sales);
        var service = ScriptedAggregator.Failure(DocumentSource.Service, ProviderStatus.Unavailable);
        var aggregator = new ScriptedAggregator(stream: (_, _) => Results(sales, service));
        using var factory = new UnifiedDocumentsApiFactory(aggregator);
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await client.SendAsync(StreamRequest("COMMERCIAL-001"));
        var events = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        events.Should().Contain("event: search.started");
        events.Should().Contain("event: source.completed");
        events.Should().Contain("\"source\":\"SALES\"");
        events.Should().Contain("event: source.failed");
        events.Should().Contain("\"source\":\"SERVICE\"");
        events.Should().Contain("event: search.completed");
        events.Should().Contain("\"status\":\"PARTIAL\"");
    }

    private static HttpRequestMessage StreamRequest(string vin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/vehicles/{vin}/documents/stream");
        request.Headers.Add("X-Dealership-Id", "42");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    private static async IAsyncEnumerable<ProviderResult> ControlledStream(
        Task<ProviderResult> sales,
        Task<ProviderResult> service,
        TaskCompletionSource<bool> started,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.TrySetResult(true);
        yield return await sales.WaitAsync(cancellationToken);
        yield return await service.WaitAsync(cancellationToken);
    }

    private static async IAsyncEnumerable<ProviderResult> Results(params ProviderResult[] results)
    {
        foreach (var result in results)
        {
            yield return result;
            await Task.Yield();
        }
    }

    private static async Task<string> ReadEventBlockAsync(StreamReader reader, string eventName, TimeSpan timeout)
    {
        var text = new StringBuilder();
        var eventStarted = false;
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(timeout);
            if (line is null) return text.ToString();
            text.AppendLine(line);
            eventStarted |= line == $"event: {eventName}";
            if (eventStarted && line.Length == 0) return text.ToString();
        }
    }
}
