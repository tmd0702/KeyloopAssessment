using FluentAssertions;
using Keyloop.UnifiedDocuments.Application;
using Keyloop.UnifiedDocuments.Infrastructure;

namespace Keyloop.UnifiedDocuments.UnitTests;

public sealed class DocumentNormalizersTests
{
    [Fact]
    public void FromSales_MapsSalesContractToCanonicalDocument()
    {
        var date = new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero);

        var document = DocumentNormalizers.FromSales("123", "Purchase agreement", "Agreement", date);

        document.Should().Be(new Document("SALES:123", "123", "Purchase agreement", "Agreement", date, DocumentSource.Sales));
    }

    [Fact]
    public void FromService_MapsServiceContractToCanonicalDocument()
    {
        var date = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero);

        var document = DocumentNormalizers.FromService("123", "Inspection report", "Inspection", date);

        document.Should().Be(new Document("SERVICE:123", "123", "Inspection report", "Inspection", date, DocumentSource.Service));
    }

    [Fact]
    public void FromProviderContracts_WhenExternalIdsMatch_GeneratesSourceSpecificStableIds()
    {
        var date = DateTimeOffset.UtcNow;
        var sales = DocumentNormalizers.FromSales("123", "Sales document", "Contract", date);
        var service = DocumentNormalizers.FromService("123", "Service document", "Repair", date);

        sales.Id.Should().Be("SALES:123");
        service.Id.Should().Be("SERVICE:123");
        sales.Id.Should().NotBe(service.Id);
    }
}