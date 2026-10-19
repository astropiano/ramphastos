using System.Text.Json.Nodes;

namespace Astropiano.Ramphastos.Protocol;

/// <summary>
/// Base envelope for all Ramphastos wire messages. Every message carries the
/// protocol version, its type discriminator, and (once established) the
/// session id plus seq/ack counters.
/// </summary>
public abstract record RamphastosMessage
{
    public int V { get; init; } = ProtocolConstants.Version;
    public abstract string Type { get; }
    public string? Session { get; init; }
    public long? Seq { get; init; }
    public long? Ack { get; init; }
}

public sealed record HelloMessage : RamphastosMessage
{
    public override string Type => MessageType.Hello;
    public string? Auth { get; init; }
    /// <summary>Resume point: last server seq the client saw.</summary>
    public long? LastSeq { get; init; }
    public string? Role { get; init; }
    /// <summary>App-defined connection identity — a window/tab id, device name,
    /// whatever the app needs to tell connections apart. Surfaced as
    /// <c>SessionConnection.Meta</c>; never interpreted by the framework.</summary>
    public JsonObject? Meta { get; init; }
}

public sealed record WelcomeMessage : RamphastosMessage
{
    public override string Type => MessageType.Welcome;
    public required string Role { get; init; }
    public long SeqBase { get; init; }
    public string? Schema { get; init; }
    public bool Replay { get; init; }
}

public sealed record SnapshotMessage : RamphastosMessage
{
    public override string Type => MessageType.Snapshot;
    /// <summary>Serialized VM graph root: {type, props, commands}.</summary>
    public required JsonNode Root { get; init; }
}

public sealed record PatchMessage : RamphastosMessage
{
    public override string Type => MessageType.Patch;
    public required List<PatchOp> Ops { get; init; }
}

public sealed record InvokeMessage : RamphastosMessage
{
    public override string Type => MessageType.Invoke;
    /// <summary>Client-side correlation id.</summary>
    public required string Id { get; init; }
    /// <summary>/vm/&lt;command&gt; for commands, /props/&lt;name&gt; for writes.</summary>
    public required string Path { get; init; }
    public JsonArray? Args { get; init; }
}

public sealed record ResultMessage : RamphastosMessage
{
    public override string Type => MessageType.Result;
    /// <summary>Echoes the invoke id.</summary>
    public required string Ref { get; init; }
    public JsonNode? Value { get; init; }
    public ErrorBody? Error { get; init; }
}

public sealed record CallMessage : RamphastosMessage
{
    public override string Type => MessageType.Call;
    public required string Id { get; init; }
    public required string Target { get; init; }
    public required string Method { get; init; }
    public JsonArray? Args { get; init; }
}

public sealed record ErrorMessage : RamphastosMessage
{
    public override string Type => MessageType.Error;
    public required ErrorBody Error { get; init; }
}

public sealed record ByeMessage : RamphastosMessage
{
    public override string Type => MessageType.Bye;
    public string? Reason { get; init; }
}

public sealed record ErrorBody
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public bool Fatal { get; init; }
    /// <summary>Set when the error refers to a specific stream.</summary>
    public string? Stream { get; init; }
}
