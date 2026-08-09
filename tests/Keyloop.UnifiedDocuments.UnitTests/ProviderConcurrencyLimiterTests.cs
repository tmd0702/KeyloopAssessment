using FluentAssertions;
using Keyloop.UnifiedDocuments.Application;
using Keyloop.UnifiedDocuments.Infrastructure;

namespace Keyloop.UnifiedDocuments.UnitTests;

public sealed class ProviderConcurrencyLimiterTests
{
    [Fact]
    public async Task Separate_provider_limiters_do_not_share_capacity()
    {
        using var sales = new ProviderConcurrencyLimiter(DocumentSource.Sales, new ProviderConcurrencyOptions { PermitLimit = 1, QueueLimit = 0, QueueTimeoutMilliseconds = 50 });
        using var service = new ProviderConcurrencyLimiter(DocumentSource.Service, new ProviderConcurrencyOptions { PermitLimit = 1, QueueLimit = 0, QueueTimeoutMilliseconds = 50 });

        using var salesLease = await sales.AcquireAsync(CancellationToken.None);
        using var serviceLease = await service.AcquireAsync(CancellationToken.None);

        salesLease!.IsAcquired.Should().BeTrue();
        serviceLease!.IsAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task Full_limiter_rejects_after_bounded_queue_wait()
    {
        using var limiter = new ProviderConcurrencyLimiter(DocumentSource.Service, new ProviderConcurrencyOptions { PermitLimit = 1, QueueLimit = 0, QueueTimeoutMilliseconds = 25 });
        using var first = await limiter.AcquireAsync(CancellationToken.None);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var second = await limiter.AcquireAsync(CancellationToken.None);

        second.Should().NotBeNull();
        second!.IsAcquired.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250));
    }
}