using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Keyloop.UnifiedDocuments.Application;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Keyloop.UnifiedDocuments.Infrastructure;

public sealed class ProviderCacheOptions
{
    public long L1SizeLimitBytes { get; init; } = 67_108_864;
    public int L1TtlSeconds { get; init; } = 30;
    public int L2TtlSeconds { get; init; } = 120;
    public int EmptyResultTtlSeconds { get; init; } = 30;
    public int TtlJitterPercent { get; init; } = 10;
    public string KeyPrefix { get; init; } = "documents";
    public DistributedLockOptions DistributedLock { get; init; } = new();
}

public sealed class DistributedLockOptions
{
    public bool Enabled { get; init; } = true;
    public int LeaseSeconds { get; init; } = 30;
    public int WaitSeconds { get; init; } = 10;
    public int RetryDelayMilliseconds { get; init; } = 100;
}

public sealed class DocumentCacheKeyFactory(string prefix = "documents")
{
    public string Create(DocumentSource source, DocumentLookup lookup) => $"{prefix}:{source.ToString().ToUpperInvariant()}:{lookup.DealershipId}:{lookup.Vin}";
    public string CreateLock(DocumentSource source, DocumentLookup lookup) => $"lock:{Create(source, lookup)}";
}

public sealed record CachedProviderDocuments(DocumentSource Provider, IReadOnlyList<Document> Documents, DateTimeOffset CachedAtUtc, DateTimeOffset ExpiresAtUtc);

public interface IDistributedDocumentLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lease, CancellationToken cancellationToken);
}

public sealed class RedisDistributedDocumentLock(IConnectionMultiplexer multiplexer) : IDistributedDocumentLock
{
    private const string ReleaseScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    public async Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lease, CancellationToken cancellationToken)
    {
        var database = multiplexer.GetDatabase();
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var acquired = await database.StringSetAsync(key, token, lease, When.NotExists).WaitAsync(cancellationToken);
        return acquired ? new Lease(database, key, token) : null;
    }

    private sealed class Lease(IDatabase database, RedisKey key, RedisValue token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await database.ScriptEvaluateAsync(ReleaseScript, [key], [token]);
    }
}

public sealed class ProviderResultCache : IDisposable
{
    public const string MeterName = "Keyloop.UnifiedDocuments.Cache";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("provider_cache_requests_total");
    private static readonly Histogram<double> LookupDuration = Meter.CreateHistogram<double>("provider_cache_lookup_duration", "s");
    private static readonly Counter<long> Errors = Meter.CreateCounter<long>("provider_cache_errors_total");
    private static readonly Counter<long> OriginFetches = Meter.CreateCounter<long>("provider_origin_fetch_total");
    private static readonly Counter<long> LockAcquire = Meter.CreateCounter<long>("distributed_lock_acquire_total");
    private static readonly Counter<long> LockContention = Meter.CreateCounter<long>("distributed_lock_contention_total");
    private static readonly Histogram<double> LockWait = Meter.CreateHistogram<double>("distributed_lock_wait_duration", "s");
    private static readonly Counter<long> LockFallback = Meter.CreateCounter<long>("distributed_lock_fallback_total");
    private static readonly Counter<long> LockFailures = Meter.CreateCounter<long>("distributed_lock_failures_total");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly MemoryCache _l1;
    private readonly IDistributedCache _redis;
    private readonly IDistributedDocumentLock _distributedLock;
    private readonly ProviderCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProviderResultCache> _logger;
    private readonly DocumentCacheKeyFactory _keys;
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    public ProviderResultCache(IDistributedCache redis, IDistributedDocumentLock distributedLock, ProviderCacheOptions options, TimeProvider timeProvider, ILogger<ProviderResultCache> logger, DocumentCacheKeyFactory? keys = null)
    {
        _redis = redis;
        _distributedLock = distributedLock;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _keys = keys ?? new DocumentCacheKeyFactory(options.KeyPrefix);
        _l1 = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.L1SizeLimitBytes });
    }

    public long L1SizeLimitBytes => _options.L1SizeLimitBytes;

    public async Task<ProviderResult> GetOrFetchAsync(IDocumentProvider origin, DocumentLookup lookup, CancellationToken cancellationToken)
    {
        var key = _keys.Create(origin.Source, lookup);
        if (TryGetL1(key, origin.Source, lookup.DealershipId, out var l1)) return l1;

        await using var localLease = await AcquireLocalAsync(key, cancellationToken);
        if (TryGetL1(key, origin.Source, lookup.DealershipId, out l1)) return l1;

        var initial = await TryGetRedisAsync(key, origin.Source, lookup.DealershipId, cancellationToken);
        if (initial.Value is not null) return PromoteRedis(key, origin.Source, initial.Value);
        if (!initial.Available || !_options.DistributedLock.Enabled) return await FetchAndCacheAsync(origin, lookup, key, cancellationToken);

        return await FetchWithDistributedLockAsync(origin, lookup, key, cancellationToken);
    }

    private async Task<ProviderResult> FetchWithDistributedLockAsync(IDocumentProvider origin, DocumentLookup lookup, string key, CancellationToken cancellationToken)
    {
        var provider = origin.Source.ToString().ToLowerInvariant();
        var lockKey = _keys.CreateLock(origin.Source, lookup);
        var stopwatch = Stopwatch.StartNew();
        var waitBudget = TimeSpan.FromSeconds(_options.DistributedLock.WaitSeconds);
        while (stopwatch.Elapsed < waitBudget)
        {
            IAsyncDisposable? lease;
            try { lease = await _distributedLock.TryAcquireAsync(lockKey, TimeSpan.FromSeconds(_options.DistributedLock.LeaseSeconds), cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LockFailures.Add(1, new TagList { { "provider", provider }, { "result", "failure" } });
                _logger.LogWarning(exception, "Distributed lock operation failed {EventName} {Provider} {DealershipId} {LockResult}", "DistributedLockOperationFailed", origin.Source.ToString().ToUpperInvariant(), lookup.DealershipId, "failure");
                return await FetchAndCacheAsync(origin, lookup, key, cancellationToken);
            }

            if (lease is not null)
            {
                await using (lease)
                {
                    LockAcquire.Add(1, new TagList { { "provider", provider }, { "result", "acquired" } });
                    _logger.LogInformation("Distributed lock acquired {EventName} {Provider} {DealershipId} {LockWaitMs} {LockResult}", "DistributedLockAcquired", origin.Source.ToString().ToUpperInvariant(), lookup.DealershipId, stopwatch.Elapsed.TotalMilliseconds, "acquired");
                    using var activity = UnifiedDocumentTelemetry.Activities.StartActivity("cache.distributed_lock");
                    activity?.SetTag("provider", provider);
                    activity?.SetTag("lock.result", "acquired");
                    var afterLock = await TryGetRedisAsync(key, origin.Source, lookup.DealershipId, cancellationToken);
                    if (afterLock.Value is not null) return PromoteRedis(key, origin.Source, afterLock.Value);
                    if (!afterLock.Available) return await FetchAndCacheAsync(origin, lookup, key, cancellationToken);
                    var result = await FetchAndCacheAsync(origin, lookup, key, cancellationToken);
                    _logger.LogInformation("Distributed lock released {EventName} {Provider} {DealershipId} {LockResult}", "DistributedLockReleased", origin.Source.ToString().ToUpperInvariant(), lookup.DealershipId, "released");
                    return result;
                }
            }

            LockContention.Add(1, new TagList { { "provider", provider }, { "result", "contended" } });
            _logger.LogInformation("Distributed lock contention {EventName} {Provider} {DealershipId} {LockWaitMs} {LockResult}", "DistributedLockContention", origin.Source.ToString().ToUpperInvariant(), lookup.DealershipId, stopwatch.Elapsed.TotalMilliseconds, "contended");
            await Task.Delay(TimeSpan.FromMilliseconds(_options.DistributedLock.RetryDelayMilliseconds), cancellationToken);
            var waiting = await TryGetRedisAsync(key, origin.Source, lookup.DealershipId, cancellationToken);
            if (waiting.Value is not null)
            {
                LockWait.Record(stopwatch.Elapsed.TotalSeconds, new TagList { { "provider", provider } });
                _logger.LogInformation("Distributed lock wait completed {EventName} {Provider} {DealershipId} {LockWaitMs} {LockResult}", "DistributedLockWaitCompleted", origin.Source.ToString().ToUpperInvariant(), lookup.DealershipId, stopwatch.Elapsed.TotalMilliseconds, "cache-hit");
                return PromoteRedis(key, origin.Source, waiting.Value);
            }
            if (!waiting.Available) return await FetchAndCacheAsync(origin, lookup, key, cancellationToken);
        }

        LockWait.Record(stopwatch.Elapsed.TotalSeconds, new TagList { { "provider", provider } });
        LockFallback.Add(1, new TagList { { "provider", provider }, { "result", "fallback" } });
        _logger.LogWarning("Distributed lock wait timed out {EventName} {Provider} {DealershipId} {LockWaitMs} {LockResult}", "DistributedLockWaitTimedOut", origin.Source.ToString().ToUpperInvariant(), lookup.DealershipId, stopwatch.Elapsed.TotalMilliseconds, "fallback");
        return await FetchAndCacheAsync(origin, lookup, key, cancellationToken);
    }

    private async Task<ProviderResult> FetchAndCacheAsync(IDocumentProvider origin, DocumentLookup lookup, string key, CancellationToken cancellationToken)
    {
        OriginFetches.Add(1, new TagList { { "source", origin.Source.ToString().ToLowerInvariant() } });
        var result = await origin.GetDocumentsAsync(lookup, cancellationToken);
        if (result.Status != ProviderStatus.Success) return result;
        var now = _timeProvider.GetUtcNow();
        var ttl = result.Documents.Count == 0 ? _options.EmptyResultTtlSeconds : _options.L2TtlSeconds;
        var envelope = new CachedProviderDocuments(origin.Source, result.Documents, now, now.AddSeconds(ApplyJitter(ttl)));
        var serialized = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        await TrySetRedisAsync(key, serialized, envelope.ExpiresAtUtc, origin.Source, lookup.DealershipId, cancellationToken);
        SetL1(key, envelope, MinL1Expiry(envelope.ExpiresAtUtc), serialized);
        return result;
    }

    private ProviderResult PromoteRedis(string key, DocumentSource source, CachedProviderDocuments envelope)
    {
        SetL1(key, envelope, MinL1Expiry(envelope.ExpiresAtUtc));
        return ProviderResult.Success(source, envelope.Documents, TimeSpan.Zero);
    }

    private bool TryGetL1(string key, DocumentSource source, int dealershipId, out ProviderResult result)
    {
        if (_l1.TryGetValue(key, out CachedProviderDocuments? envelope) && envelope!.ExpiresAtUtc > _timeProvider.GetUtcNow())
        {
            Record(source, "l1", "hit");
            _logger.LogInformation("Cache hit {EventName} {Provider} {DealershipId} {CacheLevel} {CacheResult}", "CacheHit", source.ToString().ToUpperInvariant(), dealershipId, "L1", "hit");
            result = ProviderResult.Success(source, envelope.Documents, TimeSpan.Zero);
            return true;
        }
        Record(source, "l1", "miss");
        _logger.LogInformation("Cache miss {EventName} {Provider} {DealershipId} {CacheLevel} {CacheResult}", "CacheMiss", source.ToString().ToUpperInvariant(), dealershipId, "L1", "miss");
        result = null!;
        return false;
    }

    private async Task<RedisLookup> TryGetRedisAsync(string key, DocumentSource source, int dealershipId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var bytes = await _redis.GetAsync(key, cancellationToken);
            LookupDuration.Record(stopwatch.Elapsed.TotalSeconds, new TagList { { "layer", "redis" } });
            if (bytes is null) { Record(source, "redis", "miss"); return new(null, true); }
            var envelope = JsonSerializer.Deserialize<CachedProviderDocuments>(bytes, Json);
            if (envelope is null || envelope.Provider != source || envelope.ExpiresAtUtc <= _timeProvider.GetUtcNow()) { await _redis.RemoveAsync(key, cancellationToken); return new(null, true); }
            Record(source, "redis", "hit");
            _logger.LogInformation("Cache hit {EventName} {Provider} {DealershipId} {CacheLevel} {CacheResult}", "CacheHit", source.ToString().ToUpperInvariant(), dealershipId, "L2", "hit");
            return new(envelope, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Errors.Add(1, new TagList { { "layer", "redis" }, { "operation", "read" } });
            _logger.LogWarning(exception, "Cache operation failed {EventName} {CacheLevel} {CacheOperation}; continuing to origin provider.", "CacheOperationFailed", "L2", "read");
            return new(null, false);
        }
    }

    private async Task TrySetRedisAsync(string key, byte[] value, DateTimeOffset expiry, DocumentSource source, int dealershipId, CancellationToken cancellationToken)
    {
        try { await _redis.SetAsync(key, value, new DistributedCacheEntryOptions { AbsoluteExpiration = expiry }, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Errors.Add(1, new TagList { { "layer", "redis" }, { "operation", "write" } });
            _logger.LogWarning(exception, "Cache operation failed {EventName} {Provider} {DealershipId} {CacheLevel} {CacheOperation}; provider result will still be returned.", "CacheOperationFailed", source.ToString().ToUpperInvariant(), dealershipId, "L2", "write");
        }
    }

    private void SetL1(string key, CachedProviderDocuments envelope, DateTimeOffset expiry, byte[]? serialized = null)
    {
        if (expiry <= _timeProvider.GetUtcNow()) return;
        serialized ??= JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        _l1.Set(key, envelope, new MemoryCacheEntryOptions().SetSize(Math.Max(1, serialized.Length)).SetAbsoluteExpiration(expiry));
    }

    private DateTimeOffset MinL1Expiry(DateTimeOffset l2Expiry) => new[] { l2Expiry, _timeProvider.GetUtcNow().AddSeconds(ApplyJitter(_options.L1TtlSeconds)) }.Min();
    private double ApplyJitter(int seconds) => seconds * (1 - (Random.Shared.NextDouble() * _options.TtlJitterPercent / 100d));
    private static void Record(DocumentSource source, string layer, string result) => Requests.Add(1, new TagList { { "source", source.ToString().ToLowerInvariant() }, { "layer", layer }, { "result", result } });
    private async ValueTask<Lease> AcquireLocalAsync(string key, CancellationToken cancellationToken) { var gate = _gates.GetOrAdd(key, _ => new Gate()); Interlocked.Increment(ref gate.RefCount); try { await gate.Semaphore.WaitAsync(cancellationToken); } catch { Interlocked.Decrement(ref gate.RefCount); throw; } return new(this, key, gate); }
    private void ReleaseLocal(string key, Gate gate) { gate.Semaphore.Release(); if (Interlocked.Decrement(ref gate.RefCount) == 0 && _gates.TryRemove(new KeyValuePair<string, Gate>(key, gate))) gate.Semaphore.Dispose(); }
    public void Dispose() => _l1.Dispose();
    private sealed record RedisLookup(CachedProviderDocuments? Value, bool Available);
    private sealed class Gate { public readonly SemaphoreSlim Semaphore = new(1, 1); public int RefCount; }
    private sealed class Lease(ProviderResultCache owner, string key, Gate gate) : IAsyncDisposable { public ValueTask DisposeAsync() { owner.ReleaseLocal(key, gate); return ValueTask.CompletedTask; } }
}

public sealed class CachedDocumentProvider(IDocumentProvider origin, ProviderResultCache cache) : IDocumentProvider
{
    public DocumentSource Source => origin.Source;
    public Task<ProviderResult> GetDocumentsAsync(DocumentLookup lookup, CancellationToken cancellationToken) => cache.GetOrFetchAsync(origin, lookup, cancellationToken);
}