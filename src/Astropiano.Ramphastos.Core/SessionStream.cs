using Astropiano.Ramphastos.Protocol;

namespace Astropiano.Ramphastos.Core;

/// <summary>
/// A declared stream endpoint on a session (§ data plane). The application
/// declares streams before clients attach; a client stream-open resolves
/// against the declaration.
/// </summary>
public sealed record StreamDeclaration
{
    public required string Name { get; init; }
    /// <summary>Down | Up | Duplex — from the server's perspective of data flow.</summary>
    public required string Direction { get; init; }
    /// <summary>
    /// Sink for client→server payloads; runs inside the session actor turn,
    /// so writes arrive in frame order. Only invoked when the opener is a
    /// controller and the direction allows upstream.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, ValueTask>? OnData { get; init; }
    /// <summary>Invoked inside the actor turn when the stream closes.</summary>
    public Func<string?, ValueTask>? OnClose { get; init; }
}

/// <summary>
/// One open stream on a session. Server code writes downstream via
/// <see cref="WriteAsync"/>; upstream payloads reach the declaration's
/// OnData sink. Streams are live resources — they do not persist; a
/// resumed session re-opens them.
/// </summary>
public sealed class SessionStream
{
    private readonly Session _session;

    internal SessionStream(Session session, uint id, StreamDeclaration declaration)
    {
        _session = session;
        Id = id;
        Name = declaration.Name;
        Direction = declaration.Direction;
        Declaration = declaration;
    }

    public uint Id { get; }
    public string Name { get; }
    public string Direction { get; }
    internal StreamDeclaration Declaration { get; }
    internal uint NextSeq;

    /// <summary>
    /// Queue a downstream payload (server → all connections). Safe from any
    /// thread — the write is serialized through the session actor, so seq
    /// numbering stays consistent.
    /// </summary>
    public void Write(ReadOnlyMemory<byte> payload) =>
        _session.EnqueueStreamWrite(this, payload.ToArray());

    /// <summary>Close the stream and notify all connections.</summary>
    public void Close(string? reason = null) => _session.EnqueueStreamClose(this, reason);
}
