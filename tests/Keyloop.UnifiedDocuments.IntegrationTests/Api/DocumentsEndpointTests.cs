using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using FluentAssertions;
using Keyloop.UnifiedDocuments.Application;

namespace Keyloop.UnifiedDocuments.IntegrationTests.Api;

public sealed class DocumentsEndpointTests
{
    [Fact]
    public async Task GetDocuments_WhenBothProvidersSucceed_ReturnsCompleteUnifiedResponse()
    {
        var sales = ScriptedAggregator.Success(DocumentSource.Sales, [ScriptedAggregator.Document(DocumentSource.Sales, "sales", 1)]);
        var service = ScriptedAggregator.Success(DocumentSource.Service, [ScriptedAggregator.Document(DocumentSource.Service, "service", 3)]);
        using var factory = new UnifiedDocumentsApiFactory(new ScriptedAggregator((lookup, _) => Task.FromResult(ScriptedAggregator.Result(lookup.Vin, sales, service))));
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await GetAsync(client, "COMMERCIAL-001", "42");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.RootElement.GetProperty("status").GetString().Should().Be("Complete");
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(2);
        body.RootElement.GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("id").GetString()).Should().Equal("SERVICE:service", "SALES:sales");
        body.RootElement.GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("source").GetString()).Should().Equal("Service", "Sales");
    }

    [Fact]
    public async Task GetDocuments_WhenServiceFails_ReturnsPartialSalesResponse()
    {
        var sales = ScriptedAggregator.Success(DocumentSource.Sales);
        var service = ScriptedAggregator.Failure(DocumentSource.Service, ProviderStatus.Unavailable);
        using var factory = new UnifiedDocumentsApiFactory(new ScriptedAggregator((lookup, _) => Task.FromResult(ScriptedAggregator.Result(lookup.Vin, sales, service))));
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await GetAsync(client, "COMMERCIAL-001", "42");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.RootElement.GetProperty("status").GetString().Should().Be("Partial");
        body.RootElement.GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("source").GetString()).Should().OnlyContain(source => source == "Sales");
        body.RootElement.GetProperty("sources").EnumerateArray().Single(source => source.GetProperty("source").GetString() == "Service").GetProperty("status").GetString().Should().Be("Unavailable");
    }

    [Fact]
    public async Task GetDocuments_WhenSalesFails_ReturnsPartialServiceResponse()
    {
        var sales = ScriptedAggregator.Failure(DocumentSource.Sales, ProviderStatus.Timeout);
        var service = ScriptedAggregator.Success(DocumentSource.Service);
        using var factory = new UnifiedDocumentsApiFactory(new ScriptedAggregator((lookup, _) => Task.FromResult(ScriptedAggregator.Result(lookup.Vin, sales, service))));
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await GetAsync(client, "COMMERCIAL-001", "42");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.RootElement.GetProperty("status").GetString().Should().Be("Partial");
        body.RootElement.GetProperty("documents")[0].GetProperty("source").GetString().Should().Be("Service");
    }

    [Fact]
    public async Task GetDocuments_WhenBothProvidersFail_ReturnsGatewayProblemWithoutInternalDetails()
    {
        var sales = ScriptedAggregator.Failure(DocumentSource.Sales, ProviderStatus.Unavailable);
        var service = ScriptedAggregator.Failure(DocumentSource.Service, ProviderStatus.AuthenticationFailed);
        using var factory = new UnifiedDocumentsApiFactory(new ScriptedAggregator((lookup, _) => Task.FromResult(ScriptedAggregator.Result(lookup.Vin, sales, service))));
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await GetAsync(client, "COMMERCIAL-001", "42");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        body.Should().Contain("Document providers are unavailable.");
        body.Should().NotContain("AuthenticationFailed");
        body.Should().NotContain("stack");
    }

    [Fact]
    public async Task GetDocuments_SameVinDifferentDealerships_ForwardsIsolatedLookups()
    {
        var dealerships = new ConcurrentBag<int>();
        var aggregator = new ScriptedAggregator((lookup, _) =>
        {
            dealerships.Add(lookup.DealershipId);
            var sales = ScriptedAggregator.Success(DocumentSource.Sales, [ScriptedAggregator.Document(DocumentSource.Sales, $"dealer-{lookup.DealershipId}", 1)]);
            return Task.FromResult(ScriptedAggregator.Result(lookup.Vin, sales, ScriptedAggregator.Success(DocumentSource.Service, [])));
        });
        using var factory = new UnifiedDocumentsApiFactory(aggregator);
        using var client = await factory.CreateAuthorizedClientAsync();

        var first = await GetAsync(client, "VIN-X", "42");
        var second = await GetAsync(client, "VIN-X", "99");
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        firstBody.RootElement.GetProperty("documents")[0].GetProperty("id").GetString().Should().Be("SALES:dealer-42");
        secondBody.RootElement.GetProperty("documents")[0].GetProperty("id").GetString().Should().Be("SALES:dealer-99");
        dealerships.Should().BeEquivalentTo([42, 99]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("dealership")]
    public async Task GetDocuments_WhenDealershipIdIsInvalid_ReturnsValidationProblem(string? dealershipId)
    {
        using var factory = new UnifiedDocumentsApiFactory(new ScriptedAggregator());
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await GetAsync(client, "COMMERCIAL-001", dealershipId);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("errors").TryGetProperty("X-Dealership-Id", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("VIN with spaces")]
    [InlineData("123456789012345678901234567890123")]
    public async Task GetDocuments_WhenVinIsInvalid_ReturnsValidationProblem(string vin)
    {
        using var factory = new UnifiedDocumentsApiFactory(new ScriptedAggregator());
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await GetAsync(client, vin, "42");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("errors").TryGetProperty("vin", out _).Should().BeTrue();
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string vin, string? dealershipId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/vehicles/{Uri.EscapeDataString(vin)}/documents");
        if (dealershipId is not null) request.Headers.Add("X-Dealership-Id", dealershipId);
        return client.SendAsync(request);
    }
}
