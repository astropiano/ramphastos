using Ramphastos.Core;

namespace Ramphastos.Server;

public sealed class RamphastosOptions
{
    /// <summary>Creates the application model for a new session.</summary>
    public required Func<ApplicationModel> VmFactory { get; set; }

    /// <summary>Session persistence (memory/file built in; Redis is M5).</summary>
    public ISessionProvider? Provider { get; set; }

    /// <summary>Delay before a connection-less session suspends to the provider.</summary>
    public TimeSpan DisconnectGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Envelop-level auth hook evaluated at hello (§9.1).</summary>
    public Func<Ramphastos.Protocol.HelloMessage, bool>? AuthorizeHello { get; set; }

    /// <summary>Retained patch ops per session for seq replay.</summary>
    public int OpLogCapacity { get; set; } = 500;

    /// <summary>Called once per session before the first connection attaches —
    /// where the app declares streams (session.DeclareStream).</summary>
    public Action<Session>? ConfigureSession { get; set; }

    /// <summary>Record serialization for suspended sessions (§5.4); null =
    /// JSON projection. <see cref="SessionStateCodecTotalSerializer"/> for
    /// whole-graph records.</summary>
    public ISessionStateCodec? Codec { get; set; }

    /// <summary>Schema-drift migrators for persisted records (§5.4).</summary>
    public List<ISessionMigrator> Migrators { get; } = new();

    /// <summary>Shared-object factories for [Shared] member resolution (§6.3).</summary>
    public Dictionary<string, Func<SyncedObject>> SharedObjects { get; } = new();
}
