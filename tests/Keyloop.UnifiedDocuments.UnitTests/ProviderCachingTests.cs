using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Keyloop.UnifiedDocuments.Application;
using Keyloop.UnifiedDocuments.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keyloop.UnifiedDocuments.UnitTests;

public sealed class ProviderCachingTests
{
    [Fact]
    public async Task GetOrFetchAsync_l1_hit_does_not_query_redis_or_origin()
    {
        var redis = new TestDistributedCache();
        var origin = new CountingProvider(DocumentSource.Sales, Successful(DocumentSource.Sales));
        using var cache = CreateCache(redis);

        await cache.GetOrFetchAsync(origin, new DocumentLookup("fleet-042", 42), CancellationToken.None);
        await cache.GetOrFetchAsync(origin, new DocumentLookup(" FLEET-042 ", 42), CancellationToken.None);

        origin.CallCount.Should().Be(1);
        redis.GetCount.Should().Be(2, "a cold miss is double-checked after acquiring the distributed lock");
    }

    [Fact]
    public async Task GetOrFetchAsync_redis_hit_promotes_to_l1_without_origin_fetch()
    {
        var redis = new TestDistributedCache();
        var document = Document(DocumentSource.Service);
        var envelope = new CachedProviderDocuments(DocumentSource.Service, [document], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        await redis.SetAsync(new DocumentCacheKeyFactory().Create(DocumentSource.Service, new DocumentLookup("FLEET-042", 42)), JsonSerializer.SerializeToUtf8Bytes(envelope), new DistributedCacheEntryOptions());
        var origin = new CountingProvider(DocumentSource.Service, Successful(DocumentSource.Service));
        using var cache = CreateCache(redis);

        var first = await cache.GetOrFetchAsync(origin, new DocumentLookup("fleet-042", 42), CancellationToken.None);
        var second = await cache.GetOrFetchAsync(origin, new DocumentLookup("FLEET-042", 42), CancellationToken.None);

        first.Documents.Should().ContainSingle();
        second.Documents.Should().ContainSingle();
        origin.CallCount.Should().Be(0);
        redis.GetCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrFetchAsync_l1_does_not_share_results_between_dealerships()
    {
        var redis = new TestDistributedCache();
        var origin = new CountingProvider(DocumentSource.Sales, Successful(DocumentSource.Sales));
        using var cache = CreateCache(redis);

        await cache.GetOrFetchAsync(origin, new DocumentLookup("FLEET-042", 42), CancellationToken.None);
        await cache.GetOrFetchAsync(origin, new DocumentLookup("FLEET-042", 99), CancellationToken.None);

        origin.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetOrFetchAsync_second_redis_lookup_after_lock_hit_skips_origin()
    {
        var document = Document(DocumentSource.Service);
        var envelope = new CachedProviderDocuments(DocumentSource.Service, [document], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        var redis = new TestDistributedCache { GetHandler = (_, count) => count == 2 ? JsonSerializer.SerializeToUtf8Bytes(envelope) : null };
        var origin = new CountingProvider(DocumentSource.Service, Successful(DocumentSource.Service));
        using var cache = CreateCache(redis);

        var result = await cache.GetOrFetchAsync(origin, new DocumentLookup("FLEET-042", 42), CancellationToken.None);

        result.Documents.Should().ContainSingle();
        origin.CallCount.Should().Be(0);
        redis.GetCount.Should().Be(2);
    }

    [Fact]
    public async Task GetOrFetchAsync_successful_empty_result_is_cached_but_failure_is_not()
    {
        var redis = new TestDistributedCache();
        using var cache = CreateCache(redis);
        var empty = new CountingProvider(DocumentSource.Sales, ProviderResult.Success(DocumentSource.Sales, [], TimeSpan.Zero));
        var failure = new CountingProvider(DocumentSource.Service, ProviderResult.Failure(DocumentSource.Service, ProviderStatus.Unavailable, TimeSpan.Zero));

        await cache.GetOrFetchAsync(empty, new DocumentLookup("EMPTY-VEHICLE", 42), CancellationToken.None);
        await cache.GetOrFetchAsync(empty, new DocumentLookup("EMPTY-VEHICLE", 42), CancellationToken.None);
        await cache.GetOrFetchAsync(failure, new DocumentLookup("FLEET-042", 42), CancellationToken.None);
        await cache.GetOrFetchAsync(failure, new DocumentLookup("FLEET-042", 42), CancellationToken.None);

        empty.CallCount.Should().Be(1);
        failure.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetOrFetchAsync_redis_failures_fall_back_to_origin_and_success_is_returned()
    {
        var redis = new TestDistributedCache { ThrowOnGet = true, ThrowOnSet = true };
        var origin = new CountingProvider(DocumentSource.Sales, Successful(DocumentSource.Sales));
        using var cache = CreateCache(redis);

        var result = await cache.GetOrFetchAsync(origin, new DocumentLookup("FLEET-042", 42), CancellationToken.None);

        result.Status.Should().Be(ProviderStatus.Success);
        origin.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrFetchAsync_concurrent_same_key_misses_coalesce_to_one_origin_call()
    {
        var redis = new TestDistributedCache();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var origin = new CountingProvider(DocumentSource.Sales, Successful(DocumentSource.Sales), release.Task);
        using var cache = CreateCache(redis);

        var first = cache.GetOrFetchAsync(origin, new DocumentLookup("FLEET-042", 42), CancellationToken.None);
        await origin.Started.Task;
        var second = cache.GetOrFetchAsync(origin, new DocumentLookup("FLEET-042", 42), CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(first, second);

        origin.CallCount.Should().Be(1);
        cache.L1SizeLimitBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetOrFetchAsync_multiple_pods_share_lock_then_waiters_use_redis_result()
    {
        var redis = new TestDistributedCache();
        var sharedLock = new CoordinatingLock();
        var origin = new CountingProvider(DocumentSource.Service, Successful(DocumentSource.Service), Task.Delay(50));
        using var podA = CreateCache(redis, sharedLock);
        using var podB = CreateCache(redis, sharedLock);
        using var podC = CreateCache(redis, sharedLock);
        var lookup = new DocumentLookup("VIN-X", 42);

        var results = await Task.WhenAll(
            podA.GetOrFetchAsync(origin, lookup, CancellationToken.None),
            podB.GetOrFetchAsync(origin, lookup, CancellationToken.None),
            podC.GetOrFetchAsync(origin, lookup, CancellationToken.None));

        origin.CallCount.Should().Be(1);
        results.Should().OnlyContain(result => result.Status == ProviderStatus.Success && result.Documents.Count == 1);
    }

    [Fact]
    public void Cache_and_lock_keys_include_dealership_and_provider()
    {
        var keys = new DocumentCacheKeyFactory();
        var dealership42 = new DocumentLookup("vin-x", 42);
        var dealership99 = new DocumentLookup("VIN-X", 99);
        keys.Create(DocumentSource.Sales, dealership42).Should().Be("documents:SALES:42:VIN-X");
        keys.Create(DocumentSource.Sales, dealership42).Should().NotBe(keys.Create(DocumentSource.Sales, dealership99));
        keys.Create(DocumentSource.Sales, dealership42).Should().NotBe(keys.Create(DocumentSource.Service, dealership42));
        keys.CreateLock(DocumentSource.Service, dealership42).Should().Be("lock:documents:SERVICE:42:VIN-X");
        keys.CreateLock(DocumentSource.Sales, dealership42).Should().NotBe(keys.CreateLock(DocumentSource.Service, dealership42));
    }

    private static ProviderResultCache CreateCache(TestDistributedCache redis, IDistributedDocumentLock? distributedLock = null) => new(redis, distributedLock ?? new ImmediateLock(), new ProviderCacheOptions { L1SizeLimitBytes = 1024 * 1024, L1TtlSeconds = 30, L2TtlSeconds = 120, EmptyResultTtlSeconds = 30, TtlJitterPercent = 0, DistributedLock = new DistributedLockOptions { LeaseSeconds = 30, WaitSeconds = 2, RetryDelayMilliseconds = 5 } }, TimeProvider.System, NullLogger<ProviderResultCache>.Instance);
    private static ProviderResult Successful(DocumentSource source) => ProviderResult.Success(source, [Document(source)], TimeSpan.Zero);
    private static Document Document(DocumentSource source) => new($"{source}:1", "1", "Document", "TYPE", DateTimeOffset.UtcNow, source);

    private sealed class CountingProvider(DocumentSource source, ProviderResult result, Task? waitFor = null) : IDocumentProvider
    {
        public DocumentSource Source { get; } = source;
        public int CallCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken) { CallCount++; Started.TrySetResult(); if (waitFor is not null) await waitFor.WaitAsync(cancellationToken); return result; }
    }

    private sealed class ImmediateLock : IDistributedDocumentLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lease, CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable?>(new Disposable());
        private sealed class Disposable : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class CoordinatingLock : IDistributedDocumentLock
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        public async Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lease, CancellationToken cancellationToken)
        {
            var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            return await gate.WaitAsync(0, cancellationToken) ? new Lease(gate) : null;
        }
        private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }

    private sealed class TestDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _entries = new();
        public int GetCount { get; private set; }
        public bool ThrowOnGet { get; init; }
        public bool ThrowOnSet { get; init; }
        public Func<string, int, byte[]?>? GetHandler { get; init; }
        public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) { GetCount++; if (ThrowOnGet) throw new InvalidOperationException(); return Task.FromResult(GetHandler?.Invoke(key, GetCount) ?? (_entries.TryGetValue(key, out var value) ? value : null)); }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _entries.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => SetAsync(key, value, options).GetAwaiter().GetResult();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) { if (ThrowOnSet) throw new InvalidOperationException(); _entries[key] = value; return Task.CompletedTask; }
    }
}