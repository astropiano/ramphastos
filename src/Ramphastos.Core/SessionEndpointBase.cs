using Ramphastos.Protocol;

namespace Ramphastos.Core;

/// <summary>
/// A session endpoint the manager routes to: either a server-authoritative
/// <see cref="Session"/> (VM lives in-process) or a <see cref="ClientSession"/>
/// (a remote authority client owns the model; the server mirrors state and
/// tunnels commands/streams).
/// </summary>
public abstract class SessionEndpointBase : IAsyncDisposable
{
    public abstract string Id { get; }
    public abstract int ConnectionCount { get; }
    /// <summary>Current seq — the resume point clients send as lastSeq.</summary>
    public abstract long Seq { get; }
    /// <summary>Session state for suspend scheduling.</summary>
    public abstract SessionState State { get; }

    /// <summary>Attach a connection (authority, controller or viewer).</summary>
    internal abstract Task ConnectAsync(SessionConnection conn, long? lastSeq);
    internal abstract void Disconnect(string connId);
    /// <summary>An inbound control envelope for this session (invoke, upstream
    /// authority snapshot/patch/result, stream control, …).</summary>
    internal abstract void PostInbound(string connId, RamphastosMessage msg);
    /// <summary>A decoded inbound data-plane frame.</summary>
    internal abstract void PostFrameInbound(string connId, uint streamId, uint seq, byte[] payload);
    /// <summary>Persist if idle; caller disposes.</summary>
    internal abstract Task SuspendAsync();
    public abstract ValueTask DisposeAsync();
}
