using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiffMatchPatch;
using Astropiano.Ramphastos.Protocol;

namespace Astropiano.Ramphastos.TestSdk;

/// <summary>
/// A loopback Ramphastos client: mirrors the VM graph by applying snapshot +
/// patch messages exactly like ramphastos-client does (including textpatch
/// via diff_match_patch). One TestClient == one connection.
/// </summary>
public sealed class TestClient
{
    private static readonly diff_match_patch Dmp = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TestStream>> _pendingStreams = new();
    private readonly ConcurrentQueue<TaskCompletionSource<InvokeMessage>> _invokeWaiters = new();
    private readonly ConcurrentQueue<TaskCompletionSource<StreamOpenMessage>> _streamReqWaiters = new();

    internal TestClient(string connectionId) => ConnectionId = connectionId;

    public string ConnectionId { get; }
    public string? SessionId { get; private set; }
    public string? Role { get; private set; }
    public bool LastConnectReplayed { get; private set; }
    public string? Schema { get; private set; }

    /// <summary>Mirrored VM graph (the snapshot's "props", kept up to date by patches).</summary>
    public JsonObject State { get; private set; } = new();

    /// <summary>All server messages seen, in order (welcome/snapshot/patch/…).</summary>
    public List<RamphastosMessage> Messages { get; } = new();

    /// <summary>Every patch op applied so far.</summary>
    public List<PatchOp> AppliedOps { get; } = new();

    public ErrorBody? FatalError { get; private set; }

    /// <summary>Open streams by wire stream id.</summary>
    public ConcurrentDictionary<uint, TestStream> Streams { get; } = new();

    /// <summary>Non-fatal errors seen (e.g. stream rejections).</summary>
    public List<ErrorBody> Errors { get; } = new();

    /// <summary>Invokes forwarded to this connection (authority role, §7).</summary>
    public List<InvokeMessage> Invokes { get; } = new();

    /// <summary>Stream-open requests forwarded to this connection (authority role).</summary>
    public List<StreamOpenMessage> StreamRequests { get; } = new();

    /// <summary>Wait for the next forwarded invoke (authority role).</summary>
    public Task<InvokeMessage> ExpectInvoke()
    {
        var tcs = new TaskCompletionSource<InvokeMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _invokeWaiters.Enqueue(tcs);
        return tcs.Task;
    }

    /// <summary>Wait for the next forwarded stream-open request (authority role).</summary>
    public Task<StreamOpenMessage> ExpectStreamRequest()
    {
        var tcs = new TaskCompletionSource<StreamOpenMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _streamReqWaiters.Enqueue(tcs);
        return tcs.Task;
    }

    /// <summary>Read a value at a JSON-Pointer path ("" = whole graph).</summary>
    public JsonNode? At(string pointer)
    {
        JsonNode? node = State;
        if (pointer is "" or "/") return node;
        foreach (var seg in pointer.Split('/').Skip(1).Select(JsonPointer.Unescape))
        {
            node = node switch
            {
                JsonObject o => o[seg],
                JsonArray a => int.TryParse(seg, out var i) && i < a.Count ? a[i] : null,
                _ => null,
            };
        }
        return node;
    }

    internal void Deliver(RamphastosMessage m)
    {
        Messages.Add(m);
        switch (m)
        {
            case WelcomeMessage w:
                SessionId = w.Session;
                Role = w.Role;
                Schema = w.Schema;
                LastConnectReplayed = w.Replay;
                break;
            case SnapshotMessage snap:
                State = (JsonObject)snap.Root["props"]!.DeepClone();
                break;
            case PatchMessage patch:
                foreach (var op in patch.Ops)
                {
                    ApplyOp(op);
                    AppliedOps.Add(op);
                }
                break;
            case ResultMessage r:
                if (_pending.TryRemove(r.Ref, out var tcs))
                {
                    if (r.Error is not null)
                        tcs.TrySetException(new RamphastosTestException(r.Error));
                    else
                        tcs.TrySetResult(r.Value);
                }
                break;
            case StreamOpenedMessage opened:
            {
                var stream = new TestStream(opened.Stream, (uint)opened.StreamId, opened.Direction);
                Streams[stream.StreamId] = stream;
                if (_pendingStreams.TryRemove(opened.Stream, out var openTcs))
                    openTcs.TrySetResult(stream);
                break;
            }
            case StreamOpenMessage announced when announced.StreamId is { } id:
                // peer/server-initiated announcement
                Streams[(uint)id] = new TestStream(announced.Stream, (uint)id, announced.Direction);
                break;
            case StreamOpenMessage request:
                // stream-open request forwarded to the authority
                StreamRequests.Add(request);
                if (_streamReqWaiters.TryDequeue(out var reqWaiter))
                    reqWaiter.TrySetResult(request);
                break;
            case InvokeMessage invoke:
                // invoke forwarded to the authority
                Invokes.Add(invoke);
                if (_invokeWaiters.TryDequeue(out var invWaiter))
                    invWaiter.TrySetResult(invoke);
                break;
            case StreamCloseMessage closed:
                if (closed.StreamId is { } closedId &&
                    Streams.TryRemove((uint)closedId, out var gone))
                    gone.ClosedReason = closed.Reason ?? "";
                break;
            case ErrorMessage e when e.Error.Fatal:
                FatalError = e.Error;
                break;
            case ErrorMessage e:
                Errors.Add(e.Error);
                if (e.Error.Stream is { } sname &&
                    _pendingStreams.TryRemove(sname, out var rejected))
                    rejected.TrySetException(new RamphastosTestException(e.Error));
                break;
        }
    }

    /// <summary>Data-plane delivery: decode the frame, append to the stream's log.</summary>
    internal void DeliverFrame(byte[] frame)
    {
        if (!StreamFrame.TryDecode(frame, out var streamId, out var seq, out var payload))
            return;
        if (Streams.TryGetValue(streamId, out var stream))
            stream.Frames.Add((seq, payload.ToArray()));
    }

    internal Task<TestStream> ExpectStreamOpened(string name)
    {
        var tcs = new TaskCompletionSource<TestStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStreams[name] = tcs;
        return tcs.Task;
    }

    private void ApplyOp(PatchOp op)
    {
        var segs = op.Path.Split('/').Skip(1).Select(JsonPointer.Unescape).ToArray();
        var parent = State as JsonNode;
        for (var i = 0; i < segs.Length - 1; i++)
        {
            parent = parent switch
            {
                JsonObject o => o[segs[i]],
                JsonArray a => a[int.Parse(segs[i])],
                _ => throw new InvalidOperationException($"Patch path broken at '{op.Path}'"),
            };
        }
        var key = segs.Length == 0 ? null : segs[^1];

        switch (op.Op)
        {
            case PatchOp.Add:
            case PatchOp.Copy:
                if (parent is JsonArray arr)
                    arr.Insert(key == "-" ? arr.Count : int.Parse(key!), op.Value?.DeepClone());
                else if (parent is JsonObject o)
                    o[key!] = op.Value?.DeepClone();
                break;
            case PatchOp.Replace:
                Set(parent, key, op.Value?.DeepClone());
                break;
            case PatchOp.TextPatch:
                if (op.Patch is not null && parent is not null && Read(parent, key) is JsonValue v &&
                    v.TryGetValue<string>(out var current))
                {
                    var patched = Dmp.patch_apply(Dmp.patch_fromText(op.Patch), current);
                    Set(parent, key, JsonValue.Create((string)patched[0]));
                }
                else
                {
                    Set(parent, key, op.Value?.DeepClone()); // Value carries the full string as fallback
                }
                break;
            case PatchOp.Remove:
                if (parent is JsonArray a2) a2.RemoveAt(int.Parse(key!));
                else if (parent is JsonObject o2) o2.Remove(key!);
                break;
            case PatchOp.Move:
                var val = At(op.From!)?.DeepClone();
                ApplyOp(new PatchOp { Op = PatchOp.Remove, Path = op.From! });
                ApplyOp(new PatchOp { Op = PatchOp.Add, Path = op.Path, Value = val });
                break;
        }
    }

    private static JsonNode? Read(JsonNode parent, string? key) => parent switch
    {
        JsonObject o => o[key!],
        JsonArray a => a[int.Parse(key!)],
        _ => null,
    };

    private static void Set(JsonNode? parent, string? key, JsonNode? value)
    {
        if (parent is JsonArray a) a[int.Parse(key!)] = value;
        else if (parent is JsonObject o) o[key!] = value;
    }

    internal Task<JsonNode?> ExpectResult(string id)
    {
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        return tcs.Task;
    }
}

public sealed class RamphastosTestException(ErrorBody error) : Exception($"{error.Code}: {error.Message}")
{
    public ErrorBody Error { get; } = error;
}

/// <summary>A stream as the client sees it: id, negotiated direction, received frames.</summary>
public sealed class TestStream(string name, uint streamId, string direction)
{
    public string Name { get; } = name;
    public uint StreamId { get; } = streamId;
    public string Direction { get; } = direction;
    /// <summary>Downstream frames received, in arrival order: (seq, payload).</summary>
    public List<(uint Seq, byte[] Payload)> Frames { get; } = new();
    public int NextClientSeq;
    /// <summary>Set when a stream-close arrives.</summary>
    public string? ClosedReason;
}
