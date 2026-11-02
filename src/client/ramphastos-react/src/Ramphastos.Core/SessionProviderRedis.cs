using System.Text.Json;
using Ramphastos.Protocol;
using StackExchange.Redis;

namespace Ramphastos.Core;

/// <summary>
/// Redis session provider (§5.3): one JSON record per key
/// "ramphastos:session:&lt;id&gt;", native key expiry when a TTL is set.
/// Multi-node: any node can load a record another node wrote — that is the
/// persistence half of live session migration (§5.4).
/// </summary>
public sealed class SessionProviderRedis : ISessionProvider
{
    private readonly IDatabase _db;
    private readonly string _keyPrefix;
    private readonly TimeSpan? _recordTtl;

    /// <param name="redis">Multiplexer owned by the application.</param>
    /// <param name="keyPrefix">Namespace for session keys.</param>
    /// <param name="recordTtl">Key expiry, refreshed on each save.</param>
    /// <param name="database">Redis logical database (-1 = connection default).</param>
    public SessionProviderRedis(
        IConnectionMultiplexer redis,
        string keyPrefix = "ramphastos:session:",
        TimeSpan? recordTtl = null,
        int database = -1)
    {
        _db = redis.GetDatabase(database);
        _keyPrefix = keyPrefix;
        _recordTtl = recordTtl;
    }

    private RedisKey KeyFor(string sessionId) => _keyPrefix + sessionId;

    public async Task<SessionRecord?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        var json = await _db.StringGetAsync(KeyFor(sessionId)).ConfigureAwait(false);
        if (json.IsNullOrEmpty) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionRecord>((byte[])json!, MessageSerializer.Options);
        }
        catch (JsonException)
        {
            return null; // corrupt record — caller falls back to a fresh session
        }
    }

    public Task SaveAsync(SessionRecord record, CancellationToken ct = default) =>
        _db.StringSetAsync(KeyFor(record.SessionId),
            JsonSerializer.Serialize(record, MessageSerializer.Options),
            expiry: _recordTtl);

    public Task DeleteAsync(string sessionId, CancellationToken ct = default) =>
        _db.KeyDeleteAsync(KeyFor(sessionId));
}
