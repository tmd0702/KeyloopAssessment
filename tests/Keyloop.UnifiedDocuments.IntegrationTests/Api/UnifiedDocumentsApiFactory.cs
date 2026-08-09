using System.Net.Http.Json;
using Keyloop.UnifiedDocuments.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Keyloop.UnifiedDocuments.IntegrationTests.Api;

public sealed class UnifiedDocumentsApiFactory(IDocumentAggregator aggregator) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-bytes",
            ["Audit:Enabled"] = "false"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDocumentAggregator>();
            services.AddSingleton(aggregator);
        });
    }

    public async Task<HttpClient> CreateAuthorizedClientAsync()
    {
        var client = CreateClient();
        var tokenResponse = await client.GetFromJsonAsync<TokenResponse>("/api/v1/auth/demo-token");
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokenResponse!.AccessToken);
        return client;
    }

    private sealed record TokenResponse(string AccessToken);
}

public sealed class ScriptedAggregator : IDocumentAggregator
{
    private readonly Func<DocumentLookup, CancellationToken, Task<DocumentSearchResult>> _search;
    private readonly Func<DocumentLookup, CancellationToken, IAsyncEnumerable<ProviderResult>> _stream;

    public ScriptedAggregator(
        Func<DocumentLookup, CancellationToken, Task<DocumentSearchResult>>? search = null,
        Func<DocumentLookup, CancellationToken, IAsyncEnumerable<ProviderResult>>? stream = null)
    {
        _search = search ?? ((lookup, _) => Task.FromResult(Result(lookup.Vin)));
        _stream = stream ?? ((_, _) => AsyncResults());
    }

    public Task<DocumentSearchResult> SearchAsync(DocumentLookup lookup, CancellationToken cancellationToken) => _search(lookup, cancellationToken);
    public IAsyncEnumerable<ProviderResult> StreamAsync(DocumentLookup lookup, CancellationToken cancellationToken) => _stream(lookup, cancellationToken);

    public static DocumentSearchResult Result(string vin, ProviderResult? sales = null, ProviderResult? service = null) =>
        DocumentAggregator.BuildResult(vin, [sales ?? Success(DocumentSource.Sales), service ?? Success(DocumentSource.Service)]);

    public static ProviderResult Success(DocumentSource source, IReadOnlyList<Document>? documents = null) =>
        ProviderResult.Success(source, documents ?? [Document(source, "1", 1)], TimeSpan.FromMilliseconds(5));

    public static ProviderResult Failure(DocumentSource source, ProviderStatus status) => ProviderResult.Failure(source, status, TimeSpan.FromMilliseconds(5));

    public static Document Document(DocumentSource source, string id, int month) =>
        new($"{source.ToString().ToUpperInvariant()}:{id}", id, $"{source} document {id}", "Report", new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero), source);

    private static async IAsyncEnumerable<ProviderResult> AsyncResults()
    {
        yield return Success(DocumentSource.Sales);
        await Task.Yield();
        yield return Success(DocumentSource.Service);
    }
}
