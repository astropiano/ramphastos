using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace Ramphastos.Protocol;

/// <summary>Which directions a stream carries data; negotiated at stream-open.</summary>
public static class StreamDirection
{
    /// <summary>Server → clients (e.g. terminal output).</summary>
    public const string Down = "down";
    /// <summary>Client → server (e.g. keystrokes). Controller role only.</summary>
    public const string Up = "up";
    public const string Duplex = "duplex";

    public static bool AllowsUp(string? direction) => direction is Up or Duplex;
    public static bool AllowsDown(string? direction) => direction is Down or Duplex;
}

/// <summary>
/// Open a named stream. Client→server requests (controller role); the server
/// also sends this to every attached connection once a stream exists so all
/// peers learn the assigned stream id.
/// </summary>
public sealed record StreamOpenMessage : RamphastosMessage
{
    public override string Type => MessageType.StreamOpen;
    /// <summary>Application-level stream name, e.g. "pty:main".</summary>
    public required string Stream { get; init; }
    /// <summary>Requested direction: down | up | duplex.</summary>
    public string Direction { get; init; } = StreamDirection.Down;
    /// <summary>Server-assigned data-plane id; set on announcements.</summary>
    public long? StreamId { get; init; }
    /// <summary>Optional open parameters (cols/rows, tty name, …).</summary>
    public JsonObject? Meta { get; init; }
}

/// <summary>Server → opener: the stream exists; frames may flow on StreamId.</summary>
public sealed record StreamOpenedMessage : RamphastosMessage
{
    public override string Type => MessageType.StreamOpened;
    public required string Stream { get; init; }
    public required long StreamId { get; init; }
    public required string Direction { get; init; }
}

public sealed record StreamCloseMessage : RamphastosMessage
{
    public override string Type => MessageType.StreamClose;
    public required string Stream { get; init; }
    public long? StreamId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Stream data-plane framing. Stream payload bytes NEVER travel in JSON
/// envelopes — they ride a fixed binary frame so transports can carry them
/// verbatim (raw WS binary, MessagePack bin) or base64 them (SignalR JSON)
/// without a format change:
///
///   [0]    u8   flags/version (currently 0)
///   [1..4] u32  streamId, little-endian
///   [5..8] u32  per-stream seq, little-endian
///   [9..]  payload
/// </summary>
public static class StreamFrame
{
    public const int HeaderSize = 9;
    public const byte Version = 0;

    public static byte[] Encode(uint streamId, uint seq, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), streamId);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), seq);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> frame, out uint streamId, out uint seq, out ReadOnlySpan<byte> payload)
    {
        streamId = seq = 0;
        payload = default;
        if (frame.Length < HeaderSize || frame[0] != Version)
            return false;
        streamId = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));
        seq = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(5));
        payload = frame[HeaderSize..];
        return true;
    }
}
