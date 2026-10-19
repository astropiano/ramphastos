using Microsoft.Extensions.Logging;
using Astropiano.Ramphastos.Protocol;

namespace Astropiano.Ramphastos.Core;

/// <summary>Session manager options.</summary>
public sealed class SessionOptions
{
    /// <summary>Grace period after the last connection drops before the session
    /// suspends (serializes to the provider and leaves memory). Default 60 s.</summary>
    public TimeSpan DisconnectGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Session persistence; null keeps suspended sessions in memory only.</summary>
    public ISessionProvider? Provider { get; init; }

    /// <summary>
    /// Record serialization for suspended sessions (§5.4). Null =
    /// <see cref="SessionStateCodecJson"/> (policy projection). Use
    /// <see cref="SessionStateCodecTotalSerializer"/> for whole-graph records
    /// that preserve internal VM wiring — the live-migration path.
    /// </summary>
    public ISessionStateCodec? Codec { get; init; }

    /// <summary>
    /// Schema-drift migrators, tried in order when a persisted record's
    /// fingerprint doesn't match the current model (§5.4).
    /// </summary>
    public List<ISessionMigrator> Migrators { get; } = new();

    /// <summary>
    /// Auth hook evaluated at hello (§9.1). Return false to reject with a fatal
    /// error before any session state is touched. Null accepts everything —
    /// SignalR transports can also authorize at the endpoint level.
    /// </summary>
    public Func<HelloMessage, bool>? AuthorizeHello { get; init; }

    /// <summary>Retained patch messages for seq-based replay on reconnect.</summary>
    public int OpLogCapacity { get; init; } = 500;

    /// <summary>
    /// Called once per created/resumed session before the first connection
    /// attaches — the hook where the application declares streams
    /// (session.DeclareStream) and wires session-level handlers.
    /// </summary>
    public Action<Session>? ConfigureSession { get; init; }

    /// <summary>
    /// Shared-object factories for [Shared] member resolution (§6.3): key is
    /// the SharedAttribute name or, by default, the member type's full name.
    /// The manager's <see cref="SharedObjectRegistry"/> materializes instances
    /// lazily on first use and owns them for the process lifetime.
    /// </summary>
    public Dictionary<string, Func<SyncedObject>> SharedObjects { get; } = new();

    public ILogger? Logger { get; init; }
}
