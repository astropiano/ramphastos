using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Ramphastos.Protocol;

namespace Ramphastos.Core;

public sealed record SessionConnection
{
    public required string Id { get; init; }
    public required string Role { get; init; }
    /// <summary>App-defined identity from the hello envelope — a window/tab id,
    /// device name, … How a "window" (a browser tab, a kiosk display) tells
    /// itself apart from other connections on the same session.</summary>
    public JsonObject? Meta { get; init; }
}

/// <summary>
/// Ambient context for the currently executing command or property write —
/// which connection invoked it. Null outside invoke turns (connection hooks,
/// timers, shared-object posts all run without a caller).
/// </summary>
public static class ConnectionContext
{
    private static readonly AsyncLocal<SessionConnection?> _current = new();

    /// <summary>The connection whose invoke is executing, or null.</summary>
    public static SessionConnection? Current => _current.Value;

    /// <summary>Set the ambient connection for the duration of the returned scope.</summary>
    internal static IDisposable Push(SessionConnection conn)
    {
        var prev = _current.Value;
        _current.Value = conn;
        return new RestoreOnDispose(prev);
    }

    private sealed class RestoreOnDispose(SessionConnection? prev) : IDisposable
    {
        public void Dispose() => _current.Value = prev;
    }
}

public enum SessionState
{
    /// <summary>At least one live connection.</summary>
    Active,
    /// <summary>No connections; held in memory awaiting resume or suspend.</summary>
    Idle,
}

/// <summary>
/// A Ramphastos session: one view-model graph plus its connections, with all
/// mutation serialized through a channel mailbox. No locks exist on
/// user objects — the actor owns the graph while it runs.
/// </summary>
public sealed class Session : SessionEndpointBase
{
    private readonly Channel<ISessionOp> _mailbox = Channel.CreateUnbounded<ISessionOp>();
    private readonly List<PatchOp> _pendingOps = new();
    private readonly object _opsGate = new();
    private readonly LinkedList<(long seq, PatchMessage msg)> _opLog = new();
    private readonly Dictionary<string, SessionConnection> _connections = new();
    private readonly GraphTracker _tracker;
    private readonly Func<string, RamphastosMessage, Task> _sendTo;
    private readonly Func<string, byte[], Task>? _sendFrameTo;
    private readonly SessionOptions _options;
    private readonly ILogger? _log;
    private readonly Task _runLoop;
    private volatile bool _inTurn;
    private long _seq;
    private uint _nextStreamId = 1;

    /// <summary>Streams the application declares (name → contract).</summary>
    private readonly Dictionary<string, StreamDeclaration> _streamDeclarations = new();
    /// <summary>Currently open streams (id → instance).</summary>
    private readonly Dictionary<uint, SessionStream> _streams = new();

    /// <summary>Shared objects this session mirrors (unsubscribed on dispose).</summary>
    private readonly List<SharedObject> _sharedSubscriptions = new();

    public override string Id { get; }
    public ApplicationModel Root { get; }
    internal VmModel Model { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public override int ConnectionCount
    {
        get { lock (_connections) return _connections.Count; }
    }

    public override SessionState State => ConnectionCount > 0 ? SessionState.Active : SessionState.Idle;

    /// <summary>Current seq — the resume point clients send as lastSeq.</summary>
    public override long Seq => _seq;

    public Session(
        ApplicationModel root,
        Func<string, RamphastosMessage, Task> sendTo,
        SessionOptions? options = null,
        string? sessionId = null,
        long seqBase = 0,
        Func<string, byte[], Task>? sendFrameTo = null)
    {
        Root = root;
        Model = VmModel.For(root.GetType());
        _sendTo = sendTo;
        _sendFrameTo = sendFrameTo;
        _options = options ?? new SessionOptions();
        _log = _options.Logger;
        Id = sessionId ?? "sess_" + Guid.NewGuid().ToString("N")[..12];
        _seq = seqBase;
        _tracker = new GraphTracker(Emit);
        _tracker.Attach(root);
        _runLoop = Task.Run(RunAsync);
    }

    /// <summary>Enqueue work onto the session actor. Callable from any thread.</summary>
    public void Post(Action action) =>
        _mailbox.Writer.TryWrite(new DelegateOp(() => { action(); return Task.CompletedTask; }));

    internal void Post(ISessionOp op) => _mailbox.Writer.TryWrite(op);

    internal override Task ConnectAsync(SessionConnection conn, long? lastSeq)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new ConnectOp(conn, lastSeq, tcs));
        return tcs.Task;
    }

    internal override void Disconnect(string connId) => Post(new DisconnectOp(connId));

    /// <summary>Route an inbound envelope to the matching actor op.</summary>
    internal override void PostInbound(string connId, RamphastosMessage msg)
    {
        switch (msg)
        {
            case InvokeMessage invoke:
                Post(new InvokeOp(connId, invoke));
                break;
            case StreamOpenMessage open:
                Post(new StreamOpenOp(connId, open));
                break;
            case StreamCloseMessage close when close.StreamId is { } sid:
                Post(new StreamCloseOp((uint)sid, close.Stream, close.Reason, connId));
                break;
            // Upstream snapshot/patch/result exist for client-authoritative
            // sessions only — a server-owned session simply drops them.
        }
    }

    internal override void PostFrameInbound(string connId, uint streamId, uint seq, byte[] payload) =>
        Post(new StreamFrameOp(connId, streamId, payload));

    /// <summary>
    /// Declare a stream endpoint before clients attach. Thread-safe; typically
    /// invoked from SessionOptions.ConfigureSession. Redeclaration replaces.
    /// </summary>
    public void DeclareStream(StreamDeclaration declaration)
    {
        lock (_streamDeclarations)
            _streamDeclarations[declaration.Name] = declaration;
    }

    /// <summary>Open a declared stream from server code; resolves with the stream
    /// (or null if undeclared) once announcements went out to all connections.</summary>
    public Task<SessionStream?> OpenStreamAsync(string name)
    {
        var tcs = new TaskCompletionSource<SessionStream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new ServerStreamOpenOp(name, tcs));
        return tcs.Task;
    }

    internal void EnqueueStreamWrite(SessionStream stream, byte[] payload) =>
        Post(new StreamWriteOp(stream, payload));

    internal void EnqueueStreamClose(SessionStream stream, string? reason) =>
        Post(new StreamCloseOp(stream.Id, stream.Name, reason, connId: null));

    /// <summary>Serialize the session record inside the actor turn (consistent graph
    /// state), then the caller disposes the session.</summary>
    internal override Task SuspendAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new SuspendOp(tcs));
        return tcs.Task;
    }

    private async Task RunAsync()
    {
        await foreach (var op in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _inTurn = true;
                await op.Execute(this).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Session {SessionId} op {Op} failed", Id, op.GetType().Name);
            }
            finally
            {
                _inTurn = false;
                await FlushAsync().ConfigureAwait(false);
            }
        }
    }

    // ---- change notification → pending ops ----

    private void Emit(PatchOp op, bool reduced)
    {
        lock (_opsGate)
        {
            if (reduced)
            {
                // [SyncReduced]: a newer op for the same path supersedes queued ones.
                _pendingOps.RemoveAll(p => p.Path == op.Path);
            }
            else if (op.Op is PatchOp.Replace or PatchOp.TextPatch)
            {
                // Last write wins within a turn: drop superseded same-path ops.
                _pendingOps.RemoveAll(p => p.Path == op.Path &&
                    p.Op is PatchOp.Replace or PatchOp.TextPatch);
            }
            _pendingOps.Add(op);
        }
        RequestFlushIfOffTurn();
    }

    private void RequestFlushIfOffTurn()
    {
        if (!_inTurn)
            _mailbox.Writer.TryWrite(FlushMarker.Instance);
    }

    // ---- shared objects ----

    /// <summary>Mirror a shared object at <paramref name="memberPath"/> ("/fleet").</summary>
    internal void TrackShared(SharedObject shared, string memberPath)
    {
        _sharedSubscriptions.Add(shared);
        shared.Subscribe(this, memberPath);
    }

    /// <summary>
    /// Splice a shared-object op into this session's patch stream, rebased to
    /// the member path. Called from the shared actor's fan-out — thread-safe
    /// through <see cref="_opsGate"/> + the off-turn flush marker.
    /// </summary>
    internal void EmitShared(string memberPath, PatchOp op, bool reduced) =>
        Emit(op with
        {
            Path = memberPath + op.Path,
            From = op.From is null ? null : memberPath + op.From,
        }, reduced);

    // ---- emit ----

    private Task FlushAsync()
    {
        List<PatchOp> ops;
        lock (_opsGate)
        {
            if (_pendingOps.Count == 0)
                return Task.CompletedTask;
            ops = new List<PatchOp>(_pendingOps);
            _pendingOps.Clear();
        }
        return FlushOpsAsync(ops);
    }

    private async Task FlushOpsAsync(List<PatchOp> ops)
    {
        var msg = new PatchMessage { Session = Id, Seq = ++_seq, Ops = ops };

        _opLog.AddLast((msg.Seq!.Value, msg));
        while (_opLog.Count > _options.OpLogCapacity)
            _opLog.RemoveFirst();

        foreach (var conn in ConnectionsSnapshot())
            await _sendTo(conn.Id, msg).ConfigureAwait(false);
    }

    private async Task Send(SessionConnection conn, RamphastosMessage msg) =>
        await _sendTo(conn.Id, msg).ConfigureAwait(false);

    private async Task SendResult(string connId, string @ref, JsonNode? value = null, ErrorBody? error = null)
    {
        var msg = new ResultMessage
        {
            Session = Id,
            Seq = ++_seq,
            Ref = @ref,
            Value = value,
            Error = error
        };
        await _sendTo(connId, msg).ConfigureAwait(false);
    }

    private List<SessionConnection> ConnectionsSnapshot()
    {
        lock (_connections)
            return _connections.Values.ToList();
    }

    // ---- streams (data plane) ----

    /// <summary>Find a stream declaration (also inside actor turns).</summary>
    private StreamDeclaration? DeclarationFor(string name)
    {
        lock (_streamDeclarations)
            return _streamDeclarations.TryGetValue(name, out var d) ? d : null;
    }

    private async Task SendFrameAsync(SessionConnection conn, uint streamId, uint seq, byte[] payload)
    {
        if (_sendFrameTo is null)
        {
            _log?.LogWarning("Session {SessionId}: stream write dropped — transport has no frame channel", Id);
            return;
        }
        await _sendFrameTo(conn.Id, StreamFrame.Encode(streamId, seq, payload)).ConfigureAwait(false);
    }

    private async Task AnnounceStreamAsync(SessionStream stream, string? exceptConnId = null)
    {
        foreach (var conn in ConnectionsSnapshot())
        {
            if (conn.Id == exceptConnId) continue; // the opener already got stream-opened
            await Send(conn, new StreamOpenMessage
            {
                Session = Id,
                Seq = ++_seq,
                Stream = stream.Name,
                StreamId = stream.Id,
                Direction = stream.Direction,
            }).ConfigureAwait(false);
        }
    }

    // ---- persistence ----

    /// <summary>Session record for the provider; call inside an actor turn.</summary>
    internal SessionRecord ToRecord()
    {
        var codec = _options.Codec ?? SessionStateCodecJson.Instance;
        return new SessionRecord
        {
            SessionId = Id,
            CreatedAt = CreatedAt,
            SavedAt = DateTimeOffset.UtcNow,
            LastSeq = _seq,
            VmType = Root.GetType().AssemblyQualifiedName ?? Root.GetType().FullName!,
            Schema = Model.SchemaFingerprint,
            StateFormat = codec.Format,
            State = codec.SerializeState(Root),
        };
    }

    // ---- ops ----

    internal interface ISessionOp
    {
        Task Execute(Session s);
    }

    private sealed class FlushMarker : ISessionOp
    {
        internal static readonly FlushMarker Instance = new();
        public Task Execute(Session s) => Task.CompletedTask;
    }

    private sealed class DelegateOp(Func<Task> work) : ISessionOp
    {
        public Task Execute(Session s) => work();
    }

    private sealed class ConnectOp(
        SessionConnection conn, long? lastSeq,
        TaskCompletionSource tcs) : ISessionOp
    {
        public async Task Execute(Session s)
        {
            lock (s._connections)
                s._connections[conn.Id] = conn;

            var canReplay = lastSeq.HasValue &&
                            s._opLog.Count > 0 &&
                            s._opLog.First!.Value.seq <= lastSeq.Value;

            await s.Send(conn, new WelcomeMessage
            {
                Session = s.Id,
                Seq = ++s._seq,
                Role = conn.Role,
                SeqBase = canReplay ? lastSeq!.Value : s._seq,
                Schema = s.Model.SchemaFingerprint,
                Replay = canReplay
            }).ConfigureAwait(false);

            if (canReplay)
            {
                foreach (var (seq, patch) in s._opLog)
                    if (seq > lastSeq!.Value)
                        await s.Send(conn, patch).ConfigureAwait(false);
            }
            else
            {
                await s.Send(conn, new SnapshotMessage
                {
                    Session = s.Id,
                    Seq = ++s._seq,
                    Root = s.Model.BuildSnapshot(s.Root)
                }).ConfigureAwait(false);
            }

            // Late joiners learn about already-open streams after their
            // snapshot/replay so the data plane arrives on a ready mirror.
            foreach (var stream in s._streams.Values)
                await s.Send(conn, new StreamOpenMessage
                {
                    Session = s.Id,
                    Seq = ++s._seq,
                    Stream = stream.Name,
                    StreamId = stream.Id,
                    Direction = stream.Direction,
                }).ConfigureAwait(false);

            // AFTER the joiner's snapshot: hook mutations reach it as patch
            // ops, so a window entity added here can't double-apply.
            s.Root.OnClientConnected(conn);
            tcs.TrySetResult();
        }
    }

    private sealed class DisconnectOp(string connId) : ISessionOp
    {
        public Task Execute(Session s)
        {
            SessionConnection? conn;
            lock (s._connections)
            {
                s._connections.TryGetValue(connId, out conn);
                s._connections.Remove(connId);
            }
            if (conn is not null)
                s.Root.OnClientDisconnected(conn);
            return Task.CompletedTask;
        }
    }

    /// <summary>Client-initiated stream-open (controller role required).</summary>
    internal sealed class StreamOpenOp(string connId, StreamOpenMessage msg) : ISessionOp
    {
        public async Task Execute(Session s)
        {
            SessionConnection? conn;
            lock (s._connections)
                s._connections.TryGetValue(connId, out conn);
            if (conn is null) return;

            async Task Reject(string code, string message) =>
                await s.Send(conn, new ErrorMessage
                {
                    Session = s.Id,
                    Seq = ++s._seq,
                    Error = new ErrorBody { Code = code, Message = message, Stream = msg.Stream }
                }).ConfigureAwait(false);

            if (conn.Role != SessionRole.Controller)
            {
                await Reject(ErrorCode.Forbidden, "Viewers may not open streams.");
                return;
            }

            var declaration = s.DeclarationFor(msg.Stream);
            if (declaration is null)
            {
                await Reject(ErrorCode.UnknownStream, $"No stream '{msg.Stream}' declared.");
                return;
            }

            // The granted direction is the DECLARED one — the client can ask
            // for less, never more.
            var granted = declaration.Direction;
            if (StreamDirection.AllowsUp(msg.Direction) && !StreamDirection.AllowsUp(granted))
            {
                await Reject(ErrorCode.StreamRejected,
                    $"Stream '{msg.Stream}' does not allow upstream data.");
                return;
            }

            var stream = new SessionStream(s, s._nextStreamId++, declaration);
            s._streams[stream.Id] = stream;

            await s.Send(conn, new StreamOpenedMessage
            {
                Session = s.Id,
                Seq = ++s._seq,
                Stream = stream.Name,
                StreamId = stream.Id,
                Direction = stream.Direction,
            }).ConfigureAwait(false);
            await s.AnnounceStreamAsync(stream, exceptConnId: connId).ConfigureAwait(false);
        }
    }

    /// <summary>Server-initiated open: the app materializes a declared stream.</summary>
    private sealed class ServerStreamOpenOp(string name, TaskCompletionSource<SessionStream?> tcs)
        : ISessionOp
    {
        public async Task Execute(Session s)
        {
            var declaration = s.DeclarationFor(name);
            if (declaration is null)
            {
                s._log?.LogWarning("Session {SessionId}: OpenStream('{Name}') — not declared", s.Id, name);
                tcs.TrySetResult(null);
                return;
            }
            var stream = new SessionStream(s, s._nextStreamId++, declaration);
            s._streams[stream.Id] = stream;
            await s.AnnounceStreamAsync(stream).ConfigureAwait(false);
            tcs.TrySetResult(stream);
        }
    }

    /// <summary>Inbound data frame: dispatch to the declaration sink in actor order.</summary>
    internal sealed class StreamFrameOp(string connId, uint streamId, byte[] payload) : ISessionOp
    {
        public async Task Execute(Session s)
        {
            SessionConnection? conn;
            lock (s._connections)
                s._connections.TryGetValue(connId, out conn);
            if (conn is null || !s._streams.TryGetValue(streamId, out var stream))
                return;

            if (conn.Role != SessionRole.Controller || !StreamDirection.AllowsUp(stream.Direction))
            {
                await s.Send(conn, new ErrorMessage
                {
                    Session = s.Id,
                    Seq = ++s._seq,
                    Error = new ErrorBody
                    {
                        Code = ErrorCode.Forbidden,
                        Message = "Upstream data not permitted on this stream.",
                        Stream = stream.Name,
                    }
                }).ConfigureAwait(false);
                return;
            }

            if (stream.Declaration.OnData is { } sink)
                await sink.Invoke(payload).ConfigureAwait(false);
        }
    }

    /// <summary>Server→clients data: seq'd frame fan-out to every connection.</summary>
    private sealed class StreamWriteOp(SessionStream stream, byte[] payload) : ISessionOp
    {
        public async Task Execute(Session s)
        {
            if (!s._streams.ContainsKey(stream.Id) || !StreamDirection.AllowsDown(stream.Direction))
                return;
            var seq = stream.NextSeq++;
            foreach (var conn in s.ConnectionsSnapshot())
                await s.SendFrameAsync(conn, stream.Id, seq, payload).ConfigureAwait(false);
        }
    }

    /// <summary>Close a stream; connId set = client-initiated, null = server-initiated.</summary>
    internal sealed class StreamCloseOp(uint streamId, string name, string? reason, string? connId)
        : ISessionOp
    {
        public async Task Execute(Session s)
        {
            if (!s._streams.TryGetValue(streamId, out var stream))
                return; // already gone — close is idempotent
            s._streams.Remove(streamId);

            if (stream.Declaration.OnClose is { } onClose)
                await onClose(reason).ConfigureAwait(false);

            foreach (var conn in s.ConnectionsSnapshot())
            {
                if (conn.Id == connId) continue; // the closer already knows
                await s.Send(conn, new StreamCloseMessage
                {
                    Session = s.Id,
                    Seq = ++s._seq,
                    Stream = name,
                    StreamId = streamId,
                    Reason = reason,
                }).ConfigureAwait(false);
            }
        }
    }

    private sealed class SuspendOp(TaskCompletionSource tcs) : ISessionOp
    {
        public async Task Execute(Session s)
        {
            if (s.ConnectionCount > 0 || s._options.Provider is null)
            {
                tcs.TrySetResult();
                return;
            }
            await s._options.Provider.SaveAsync(s.ToRecord()).ConfigureAwait(false);
            tcs.TrySetResult();
        }
    }

    internal sealed class InvokeOp(string connId, InvokeMessage msg) : ISessionOp
    {
        public async Task Execute(Session s)
        {
            SessionConnection? conn;
            lock (s._connections)
                s._connections.TryGetValue(connId, out conn);

            if (conn is null)
                return;

            if (conn.Role != SessionRole.Controller)
            {
                await s.SendResult(connId, msg.Id,
                    error: new ErrorBody { Code = ErrorCode.Forbidden, Message = "Viewers may not invoke." })
                    .ConfigureAwait(false);
                return;
            }

            JsonNode? value = null;
            ErrorBody? error = null;
            try
            {
                using (ConnectionContext.Push(conn))
                {
                    if (msg.Path.StartsWith("/vm/", StringComparison.Ordinal))
                        (value, error) = await ExecuteCommand(s, conn, msg).ConfigureAwait(false);
                    else if (msg.Path.StartsWith("/props/", StringComparison.Ordinal))
                        (value, error) = ExecuteWrite(s, msg);
                    else
                        error = new ErrorBody { Code = ErrorCode.UnknownPath, Message = msg.Path };
                }
            }
            catch (Exception ex)
            {
                error = new ErrorBody { Code = ErrorCode.InvokeFailed, Message = ex.Message };
            }

            // The patch the command produced must land before the result —
            // awaiting an invoke should observe the state it caused.
            await s.FlushAsync().ConfigureAwait(false);
            await s.SendResult(connId, msg.Id, value, error).ConfigureAwait(false);
        }

        private async Task<(JsonNode? value, ErrorBody? error)> ExecuteCommand(
            Session s, SessionConnection conn, InvokeMessage msg)
        {
            var name = JsonPointer.Unescape(msg.Path[4..]);
            if (!s.Model.Commands.TryGetValue(name, out var method))
                return (null, new ErrorBody { Code = ErrorCode.UnknownPath, Message = msg.Path });

            var attr = s.Model.CommandAttributes[name];
            if (conn.Role != attr.RequireRole)
                return (null, new ErrorBody
                {
                    Code = ErrorCode.Forbidden,
                    Message = $"Command '{name}' requires role '{attr.RequireRole}'."
                });

            var parameters = method.GetParameters();
            var args = msg.Args;
            var values = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var argNode = args is not null && i < args.Count ? args[i] : null;
                values[i] = argNode is null
                    ? (parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null)
                    : argNode.Deserialize(parameters[i].ParameterType, MessageSerializer.Options);
            }

            var result = method.Invoke(s.Root, values);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                result = method.ReturnType.IsGenericType
                    ? method.ReturnType.GetProperty("Result")?.GetValue(task)
                    : null;
            }

            return (VmSerializer.ToJson(result), null);
        }

        private static (JsonNode? value, ErrorBody? error) ExecuteWrite(Session s, InvokeMessage msg)
        {
            var name = JsonPointer.Unescape(msg.Path[7..]);
            if (!s.Model.Properties.TryGetValue(name, out var prop) || prop.Writer is null)
                return (null, new ErrorBody { Code = ErrorCode.UnknownPath, Message = msg.Path });
            if (prop.Shared)
                return (null, new ErrorBody
                {
                    Code = ErrorCode.Forbidden,
                    Message = "Shared members are server-managed; write intents do not apply."
                });

            var value = msg.Args is { Count: > 0 }
                ? VmSerializer.ToValue(msg.Args[0], prop.Info.PropertyType)
                : null;
            prop.Write(s.Root, value);

            return ((JsonNode?)null, null);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _tracker.DetachAll();
        foreach (var shared in _sharedSubscriptions)
            shared.Unsubscribe(this);
        _sharedSubscriptions.Clear();
        _mailbox.Writer.TryComplete();
        try { await _runLoop.ConfigureAwait(false); }
        catch (ChannelClosedException) { }
        if (Root is IDisposable d) d.Dispose();
    }
}
