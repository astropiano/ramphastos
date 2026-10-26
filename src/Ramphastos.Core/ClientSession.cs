using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Ramphastos.Protocol;

namespace Ramphastos.Core;

/// <summary>
/// A client-authoritative session (§4.9): the authority (e.g. a desktop app)
/// owns the real model and pushes snapshot/patch upstream; the server keeps a
/// JSON replica plus the op-log and replays them to viewers/controllers —
/// the wire facing clients is identical to a server-owned session.
///
/// Commands and stream data tunnel through: viewer/controller invokes and
/// stream-opens are forwarded to the authority connection, whose results
/// route back to the requester. Frames relay verbatim in both directions.
/// </summary>
public sealed class ClientSession : SessionEndpointBase
{
    internal interface IOp { Task Execute(ClientSession s); }

    private readonly Channel<IOp> _mailbox = Channel.CreateUnbounded<IOp>();
    private readonly Func<string, RamphastosMessage, Task> _sendTo;
    private readonly Func<string, byte[], Task>? _sendFrameTo;
    private readonly SessionOptions _options;
    private readonly ILogger? _log;
    private readonly Task _runLoop;

    private readonly Dictionary<string, SessionConnection> _connections = new();
    private readonly LinkedList<(long seq, RamphastosMessage msg)> _opLog = new();
    /// <summary>Pending client invokes: invoke id → requesting connection.</summary>
    private readonly Dictionary<string, string> _pendingInvokes = new();
    /// <summary>Pending client stream-opens per stream name (FIFO).</summary>
    private readonly Dictionary<string, Queue<string>> _pendingStreamOpens = new();
    /// <summary>Open streams: id → (name, direction).</summary>
    private readonly Dictionary<uint, (string name, string direction)> _streams = new();

    /// <summary>The client replica — the authority's state tree (mirror content).</summary>
    private JsonObject _replica = new();
    private string? _authorityConnId;
    private long _seq;
    private uint _nextStreamId = 1;

    public ClientSession(
        Func<string, RamphastosMessage, Task> sendTo,
        SessionOptions? options = null,
        string? sessionId = null,
        long seqBase = 0,
        JsonObject? replica = null,
        Func<string, byte[], Task>? sendFrameTo = null)
    {
        Id = sessionId ?? "sess_" + Guid.NewGuid().ToString("N")[..12];
        _seq = seqBase;
        _sendTo = sendTo;
        _sendFrameTo = sendFrameTo;
        _options = options ?? new SessionOptions();
        _log = _options.Logger;
        if (replica is not null) _replica = replica;
        _runLoop = Task.Run(RunAsync);
    }

    public override string Id { get; }
    public override long Seq => _seq;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Live replica of the authority's state (read inside the actor).</summary>
    public JsonObject Replica => _replica;

    /// <summary>True while an authority connection is attached.</summary>
    public bool AuthorityOnline => _authorityConnId is not null;

    public override int ConnectionCount
    {
        get { lock (_connections) return _connections.Count; }
    }

    public override SessionState State => ConnectionCount > 0 ? SessionState.Active : SessionState.Idle;

    internal void Post(IOp op) => _mailbox.Writer.TryWrite(op);

    private async Task RunAsync()
    {
        await foreach (var op in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await op.Execute(this).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log?.LogError(ex, "ClientSession {SessionId} op {Op} failed", Id, op.GetType().Name);
            }
        }
    }

    internal override Task ConnectAsync(SessionConnection conn, long? lastSeq)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new ConnectOp(conn, lastSeq, tcs));
        return tcs.Task;
    }

    internal override void Disconnect(string connId) => Post(new DisconnectOp(connId));

    internal override void PostInbound(string connId, RamphastosMessage msg) =>
        Post(new InboundOp(connId, msg));

    internal override void PostFrameInbound(string connId, uint streamId, uint seq, byte[] payload) =>
        Post(new FrameOp(connId, streamId, seq, payload));

    internal override Task SuspendAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new SuspendOp(tcs));
        return tcs.Task;
    }

    // ---- helpers ----

    private List<SessionConnection> ConnectionsSnapshot()
    {
        lock (_connections) return _connections.Values.ToList();
    }

    private Task Send(string connId, RamphastosMessage msg) =>
        _sendTo(connId, msg);

    private async Task SendAll(RamphastosMessage msg, string? exceptConnId = null)
    {
        foreach (var conn in ConnectionsSnapshot())
        {
            if (conn.Id != exceptConnId)
                await Send(conn.Id, msg).ConfigureAwait(false);
        }
    }

    private async Task SendAllExceptAuthority(RamphastosMessage msg) =>
        await SendAll(msg, _authorityConnId).ConfigureAwait(false);

    private Task SendFrame(string connId, uint streamId, uint seq, byte[] payload)
    {
        if (_sendFrameTo is null)
        {
            _log?.LogWarning("ClientSession {SessionId}: frame dropped — no frame transport", Id);
            return Task.CompletedTask;
        }
        return _sendFrameTo(connId, StreamFrame.Encode(streamId, seq, payload));
    }

    private async Task SendError(string connId, string code, string message, string? stream = null) =>
        await Send(connId, new ErrorMessage
        {
            Session = Id,
            Seq = ++_seq,
            Error = new ErrorBody { Code = code, Message = message, Stream = stream },
        }).ConfigureAwait(false);

    private Task SendToAuthority(RamphastosMessage msg) =>
        _authorityConnId is { } id ? Send(id, msg) : Task.CompletedTask;

    /// <summary>The replica wrapped for the wire — viewers see the same
    /// {type, props} shape a server-owned session produces.</summary>
    private JsonObject ReplicaForWire() => new()
    {
        ["type"] = "client",
        ["props"] = _replica.DeepClone(),
    };

    // ---- ops ----

    private sealed class ConnectOp(SessionConnection conn, long? lastSeq, TaskCompletionSource tcs) : IOp
    {
        public async Task Execute(ClientSession s)
        {
            lock (s._connections)
                s._connections[conn.Id] = conn;

            var canReplay = conn.Role != SessionRole.Authority &&
                            lastSeq.HasValue &&
                            s._opLog.Count > 0 &&
                            s._opLog.First!.Value.seq <= lastSeq.Value;

            if (conn.Role == SessionRole.Authority)
            {
                // One authority at a time — a re-attach replaces the old one.
                s._authorityConnId = conn.Id;
            }

            await s.Send(conn.Id, new WelcomeMessage
            {
                Session = s.Id,
                Seq = ++s._seq,
                Role = conn.Role,
                SeqBase = canReplay ? lastSeq!.Value : s._seq,
                Replay = canReplay,
            }).ConfigureAwait(false);

            if (conn.Role != SessionRole.Authority)
            {
                if (canReplay)
                {
                    foreach (var (seq, msg) in s._opLog)
                        if (seq > lastSeq!.Value)
                            await s.Send(conn.Id, msg).ConfigureAwait(false);
                }
                else
                {
                    await s.Send(conn.Id, new SnapshotMessage
                    {
                        Session = s.Id,
                        Seq = ++s._seq,
                        Root = s.ReplicaForWire(),
                    }).ConfigureAwait(false);
                }

                foreach (var (id, stream) in s._streams)
                    await s.Send(conn.Id, new StreamOpenMessage
                    {
                        Session = s.Id,
                        Seq = ++s._seq,
                        Stream = stream.name,
                        StreamId = id,
                        Direction = stream.direction,
                    }).ConfigureAwait(false);
            }

            tcs.TrySetResult();
        }
    }

    private sealed class DisconnectOp(string connId) : IOp
    {
        public async Task Execute(ClientSession s)
        {
            lock (s._connections)
                s._connections.Remove(connId);

            if (connId == s._authorityConnId)
            {
                s._log?.LogWarning(
                    "ClientSession {SessionId}: authority conn {ConnId} detached — session is authority_offline until re-hello",
                    s.Id, connId);
                s._authorityConnId = null;
                // Streams are live resources owned by the authority — they die
                // with its connection.
                foreach (var (id, stream) in s._streams.ToArray())
                {
                    s._streams.Remove(id);
                    await s.SendAllExceptAuthority(new StreamCloseMessage
                    {
                        Session = s.Id,
                        Seq = ++s._seq,
                        Stream = stream.name,
                        StreamId = id,
                        Reason = "authority offline",
                    }).ConfigureAwait(false);
                }
                // Pending invokes can never resolve — fail fast.
                foreach (var pending in s._pendingInvokes)
                    await s.Send(pending.Value, new ResultMessage
                    {
                        Session = s.Id,
                        Seq = ++s._seq,
                        Ref = pending.Key,
                        Error = new ErrorBody
                        {
                            Code = ErrorCode.AuthorityOffline,
                            Message = "Authority went offline.",
                        },
                    }).ConfigureAwait(false);
                s._pendingInvokes.Clear();
                s._pendingStreamOpens.Clear();
            }
        }
    }

    /// <summary>Inbound envelope dispatch by connection role + message type.</summary>
    private sealed class InboundOp(string connId, RamphastosMessage msg) : IOp
    {
        public async Task Execute(ClientSession s)
        {
            if (connId == s._authorityConnId)
                await FromAuthority(s).ConfigureAwait(false);
            else
                await FromClient(s).ConfigureAwait(false);
        }

        private async Task FromAuthority(ClientSession s)
        {
            switch (msg)
            {
                case SnapshotMessage snap:
                {
                    // root = the authority's state tree; wrap as {type:"client", props}
                    if (snap.Root is JsonObject state)
                        s._replica = (JsonObject)state.DeepClone();
                    var relay = new SnapshotMessage
                    {
                        Session = s.Id, Seq = ++s._seq, Root = s.ReplicaForWire(),
                    };
                    s.AppendLog(relay);
                    await s.SendAllExceptAuthority(relay).ConfigureAwait(false);
                    break;
                }
                case PatchMessage patch:
                {
                    var applied = new List<PatchOp>();
                    foreach (var op in patch.Ops)
                    {
                        try { JsonPatchApplier.Apply(s._replica, op); applied.Add(op); }
                        catch (JsonPatchApplier.JsonPatchException ex)
                        {
                            s._log?.LogWarning(ex, "ClientSession {SessionId}: dropped bad patch op", s.Id);
                            await s.SendError(connId, ErrorCode.SessionCorrupted,
                                $"Patch op failed to apply: {op.Path}").ConfigureAwait(false);
                        }
                    }
                    if (applied.Count == 0) break;
                    var relay = new PatchMessage { Session = s.Id, Seq = ++s._seq, Ops = applied };
                    s.AppendLog(relay);
                    await s.SendAllExceptAuthority(relay).ConfigureAwait(false);
                    break;
                }
                case ResultMessage result:
                {
                    if (s._pendingInvokes.Remove(result.Ref, out var requester))
                    {
                        await s.Send(requester, result with { Session = s.Id, Seq = ++s._seq })
                            .ConfigureAwait(false);
                    }
                    break;
                }
                case StreamOpenMessage open:
                    await s.GrantStream(open, requesterConnId: s.DequeuePendingStream(open.Stream))
                        .ConfigureAwait(false);
                    break;
                case StreamCloseMessage close:
                    await s.CloseStream((uint)(close.StreamId ?? 0), close.Reason, exceptConnId: connId)
                        .ConfigureAwait(false);
                    break;
                case ErrorMessage err when err.Error.Stream is { } streamName:
                    // Authority rejected a client stream-open — route it back.
                    if (s.DequeuePendingStream(streamName) is { } denied)
                        await s.SendError(denied, err.Error.Code, err.Error.Message, streamName)
                            .ConfigureAwait(false);
                    break;
            }
        }

        private async Task FromClient(ClientSession s)
        {
            SessionConnection? conn;
            lock (s._connections)
                s._connections.TryGetValue(connId, out conn);
            if (conn is null) return;

            switch (msg)
            {
                case InvokeMessage invoke:
                {
                    if (conn.Role != SessionRole.Controller)
                    {
                        await s.Send(connId, new ResultMessage
                        {
                            Session = s.Id, Seq = ++s._seq, Ref = invoke.Id,
                            Error = new ErrorBody
                            {
                                Code = ErrorCode.Forbidden, Message = "Viewers may not invoke.",
                            },
                        }).ConfigureAwait(false);
                        return;
                    }
                    if (s._authorityConnId is null)
                    {
                        await s.Send(connId, new ResultMessage
                        {
                            Session = s.Id, Seq = ++s._seq, Ref = invoke.Id,
                            Error = new ErrorBody
                            {
                                Code = ErrorCode.AuthorityOffline,
                                Message = "Authority is offline.",
                            },
                        }).ConfigureAwait(false);
                        return;
                    }
                    s._pendingInvokes[invoke.Id] = connId;
                    await s.SendToAuthority(invoke with { Session = s.Id }).ConfigureAwait(false);
                    break;
                }
                case StreamOpenMessage open:
                {
                    if (conn.Role != SessionRole.Controller)
                    {
                        await s.SendError(connId, ErrorCode.Forbidden,
                            "Viewers may not open streams.", open.Stream).ConfigureAwait(false);
                        return;
                    }
                    if (s._authorityConnId is null)
                    {
                        await s.SendError(connId, ErrorCode.AuthorityOffline,
                            "Authority is offline.", open.Stream).ConfigureAwait(false);
                        return;
                    }
                    s.EnqueuePendingStream(open.Stream, connId);
                    await s.SendToAuthority(open with { Session = s.Id }).ConfigureAwait(false);
                    break;
                }
                case StreamCloseMessage close:
                    await s.SendToAuthority(close with { Session = s.Id }).ConfigureAwait(false);
                    await s.CloseStream((uint)(close.StreamId ?? 0), close.Reason, exceptConnId: connId)
                        .ConfigureAwait(false);
                    break;
                default:
                    // Viewers pushing state? Forbidden — only the authority writes.
                    if (msg is SnapshotMessage or PatchMessage or ResultMessage)
                        await s.SendError(connId, ErrorCode.Forbidden,
                            "Only the authority connection may push state.").ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Data-plane relay: authority → clients, clients → authority.</summary>
    private sealed class FrameOp(string connId, uint streamId, uint seq, byte[] payload) : IOp
    {
        public async Task Execute(ClientSession s)
        {
            if (!s._streams.TryGetValue(streamId, out var stream)) return;

            if (connId == s._authorityConnId)
            {
                if (!StreamDirection.AllowsDown(stream.direction)) return;
                foreach (var conn in s.ConnectionsSnapshot())
                {
                    if (conn.Id != s._authorityConnId)
                        await s.SendFrame(conn.Id, streamId, seq, payload).ConfigureAwait(false);
                }
            }
            else
            {
                SessionConnection? conn;
                lock (s._connections)
                    s._connections.TryGetValue(connId, out conn);
                if (conn?.Role != SessionRole.Controller ||
                    !StreamDirection.AllowsUp(stream.direction) ||
                    s._authorityConnId is null)
                    return;
                await s.SendFrame(s._authorityConnId, streamId, seq, payload).ConfigureAwait(false);
            }
        }
    }

    private sealed class SuspendOp(TaskCompletionSource tcs) : IOp
    {
        public async Task Execute(ClientSession s)
        {
            if (s.ConnectionCount > 0 || s._options.Provider is null)
            {
                tcs.TrySetResult();
                return;
            }
            await s._options.Provider.SaveAsync(new SessionRecord
            {
                SessionId = s.Id,
                CreatedAt = s.CreatedAt,
                SavedAt = DateTimeOffset.UtcNow,
                LastSeq = s._seq,
                VmType = "client",
                StateFormat = "mirror",
                State = s._replica,
            }).ConfigureAwait(false);
            tcs.TrySetResult();
        }
    }

    // ---- stream bookkeeping ----

    private void EnqueuePendingStream(string name, string connId)
    {
        if (!_pendingStreamOpens.TryGetValue(name, out var q))
            _pendingStreamOpens[name] = q = new Queue<string>();
        q.Enqueue(connId);
    }

    private string? DequeuePendingStream(string name)
    {
        if (!_pendingStreamOpens.TryGetValue(name, out var q) || q.Count == 0) return null;
        var connId = q.Dequeue();
        if (q.Count == 0) _pendingStreamOpens.Remove(name);
        return connId;
    }

    /// <summary>Authority granted a stream (or opened its own): assign the id,
    /// open it for the requester, announce to everyone else.</summary>
    private async Task GrantStream(StreamOpenMessage open, string? requesterConnId)
    {
        var id = _nextStreamId++;
        var direction = open.Direction;
        _streams[id] = (open.Stream, direction);

        var opened = new StreamOpenedMessage
        {
            Session = Id, Seq = ++_seq,
            Stream = open.Stream, StreamId = id, Direction = direction,
        };
        if (requesterConnId is not null)
            await Send(requesterConnId, opened).ConfigureAwait(false);
        await SendToAuthority(opened).ConfigureAwait(false); // authority learns its stream id

        foreach (var conn in ConnectionsSnapshot())
        {
            if (conn.Id == requesterConnId || conn.Id == _authorityConnId) continue;
            await Send(conn.Id, new StreamOpenMessage
            {
                Session = Id, Seq = ++_seq,
                Stream = open.Stream, StreamId = id, Direction = direction,
            }).ConfigureAwait(false);
        }
    }

    private async Task CloseStream(uint streamId, string? reason, string? exceptConnId)
    {
        if (!_streams.Remove(streamId, out var stream)) return;
        foreach (var conn in ConnectionsSnapshot())
        {
            if (conn.Id == exceptConnId) continue;
            await Send(conn.Id, new StreamCloseMessage
            {
                Session = Id, Seq = ++_seq,
                Stream = stream.name, StreamId = streamId, Reason = reason,
            }).ConfigureAwait(false);
        }
    }

    private void AppendLog(RamphastosMessage msg)
    {
        _opLog.AddLast((msg.Seq!.Value, msg));
        while (_opLog.Count > _options.OpLogCapacity)
            _opLog.RemoveFirst();
    }

    public override async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryComplete();
        try { await _runLoop.ConfigureAwait(false); }
        catch (ChannelClosedException) { }
    }
}
