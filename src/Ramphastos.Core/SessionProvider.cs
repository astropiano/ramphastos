using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ramphastos.Protocol;

namespace Ramphastos.Core;

/// <summary>
/// Persisted session record (§5.4). State holds the full serialized graph —
/// including [ServerOnly] members, which exist in records but never on the
/// wire.
/// </summary>
public sealed record SessionRecord
{
    public required string SessionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public long LastSeq { get; init; }
    public required string VmType { get; init; }
    public string? Schema { get; init; }
    public int Protocol { get; init; } = ProtocolConstants.Version;
    /// <summary>
    /// Codec tag for <see cref="State"/>: "stj" (default when absent — the
    /// policy projection) or "ts" (TotalSerializer whole-graph blob).
    /// Resume only trusts a state whose format the configured codec can read.
    /// </summary>
    public string? StateFormat { get; init; }
    /// <summary>Serialized root state: JsonObject projection (stj) or a JsonValue string blob (ts).</summary>
    public JsonNode? State { get; init; }
    public JsonObject? Meta { get; init; }
}

/// <summary>Session persistence backend: memory for dev, files for single-node, Redis for multi-node (M5).</summary>
public interface ISessionProvider
{
    Task<SessionRecord?> LoadAsync(string sessionId, CancellationToken ct = default);
    Task SaveAsync(SessionRecord record, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
}

public sealed class SessionProviderInMemory : ISessionProvider
{
    private readonly ConcurrentDictionary<string, SessionRecord> _records = new();

    public Task<SessionRecord?> LoadAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(_records.TryGetValue(sessionId, out var r) ? r : null);

    public Task SaveAsync(SessionRecord record, CancellationToken ct = default)
    {
        _records[record.SessionId] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        _records.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>One JSON file per session record under <paramref name="directory"/>.</summary>
public sealed class SessionProviderFiles : ISessionProvider
{
    private readonly string _directory;
    private readonly TimeSpan? _recordTtl;

    /// <param name="recordTtl">Records older than this are expired on load (deleted, treated as absent).</param>
    public SessionProviderFiles(string directory, TimeSpan? recordTtl = null)
    {
        _directory = directory;
        _recordTtl = recordTtl;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(string sessionId) =>
        Path.Combine(_directory, Sanitize(sessionId) + ".json");

    private static string Sanitize(string id) =>
        string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    public async Task<SessionRecord?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) return null;

        SessionRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<SessionRecord>(
                await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
                MessageSerializer.Options);
        }
        catch (JsonException)
        {
            return null; // corrupt record — caller falls back to a fresh session
        }

        if (record is not null && _recordTtl is { } ttl && DateTimeOffset.UtcNow - record.SavedAt > ttl)
        {
            File.Delete(path);
            return null;
        }
        return record;
    }

    public async Task SaveAsync(SessionRecord record, CancellationToken ct = default)
    {
        var path = PathFor(record.SessionId);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp,
            JsonSerializer.Serialize(record, MessageSerializer.Options), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        var path = PathFor(sessionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
