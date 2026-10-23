using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Ramphastos.Protocol;

namespace Ramphastos.Core;

/// <summary>
/// Owns the session registry and routes transport events to the right
/// session actor. Transport-agnostic: outbound messages leave through the
/// injected sender delegate. Suspends sessions to the configured
/// <see cref="ISessionProvider"/> after <see cref="SessionOptions.DisconnectGrace"/>.
/// </summary>
public sealed class RamphastosSessionManager : IAsyncDisposable
{
    private readonly Func<ApplicationModel> _vmFactory;
    private readonly Func<string, RamphastosMessage, Task> _sendTo;
    private readonly Func<string, byte[], Task>? _sendFrameTo;
    private readonly SessionOptions _options;
    private readonly ConcurrentDictionary<string, SessionEndpointBase> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _connectionSessions = new();

    public RamphastosSessionManager(
        Func<ApplicationModel> vmFactory,
        Func<string, RamphastosMessage, Task> sendTo,
        SessionOptions? options = null,
        Func<string, byte[], Task>? sendFrameTo = null)
    {
        _vmFactory = vmFactory;
        _sendTo = sendTo;
        _sendFrameTo = sendFrameTo;
        _options = options ?? new SessionOptions();
        SharedObjects = new SharedObjectRegistry(_options.SharedObjects);
    }

    /// <summary>Server-owned sessions (VM lives in-process). See also <see cref="Endpoints"/>.</summary>
    public IReadOnlyCollection<Session> Sessions => _sessions.Values.OfType<Session>().ToList();

    /// <summary>All live session endpoints, server-owned and client-authoritative (§4.9).</summary>
    public IReadOnlyCollection<SessionEndpointBase> Endpoints => _sessions.Values.ToList();

    /// <summary>Process-wide shared objects (§6.3) — mutate via .Post().</summary>
    public SharedObjectRegistry SharedObjects { get; }

    /// <summary>Handle a hello: authorize, create or resume a session, attach the connection.</summary>
    public async Task HandleHello(string connectionId, HelloMessage hello)
    {
        if (_options.AuthorizeHello is { } authorize && !authorize(hello))
        {
            await _sendTo(connectionId, new ErrorMessage
            {
                Error = new ErrorBody
                {
                    Code = ErrorCode.Forbidden,
                    Message = "hello rejected",
                    Fatal = true,
                }
            }).ConfigureAwait(false);
            return;
        }

        if (hello.Role == SessionRole.Authority)
        {
            await AttachAuthority(connectionId, hello).ConfigureAwait(false);
            return;
        }

        // Fail closed on unknown role values — only the defined roles exist.
        var role = hello.Role == SessionRole.Controller
            ? SessionRole.Controller
            : SessionRole.Viewer;

        SessionEndpointBase? session = null;
        if (hello.Session is not null && _sessions.TryGetValue(hello.Session, out var existing))
        {
            session = existing;
        }
        else if (hello.Session is not null)
        {
            session = await TryResumeAsync(hello.Session).ConfigureAwait(false);
        }

        // Honor a requested id for fresh sessions too — a stable, well-known
        // session name (e.g. per-user 'axn-shared' on a per-user session host)
        // is how multiple devices converge on the same VM.
        session ??= CreateSession(_vmFactory(), sessionId: hello.Session);
        _sessions.TryAdd(session.Id, session);

        _connectionSessions[connectionId] = session.Id;
        await session.ConnectAsync(
            new SessionConnection { Id = connectionId, Role = role, Meta = hello.Meta },
            hello.LastSeq)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A client hello attaching as authority: the session is a mirrored
    /// projection — the client owns the model and pushes state upstream (§4.9).
    /// </summary>
    private async Task AttachAuthority(string connectionId, HelloMessage hello)
    {
        SessionEndpointBase? session = null;
        if (hello.Session is not null &&
            _sessions.TryGetValue(hello.Session, out var existing))
        {
            if (existing is not ClientSession)
            {
                await _sendTo(connectionId, new ErrorMessage
                {
                    Error = new ErrorBody
                    {
                        Code = ErrorCode.Forbidden,
                        Message = $"Session '{hello.Session}' is server-owned — authority attach rejected.",
                        Fatal = true,
                    },
                }).ConfigureAwait(false);
                return;
            }
            session = existing;
        }
        else
        {
            session = await TryResumeAsync(hello.Session).ConfigureAwait(false)
                ?? CreateClientSession(hello.Session);
            _sessions.TryAdd(session.Id, session);
        }

        _connectionSessions[connectionId] = session.Id;
        await session.ConnectAsync(
            new SessionConnection
            {
                Id = connectionId, Role = SessionRole.Authority, Meta = hello.Meta,
            },
            hello.LastSeq)
            .ConfigureAwait(false);
    }

    private ClientSession CreateClientSession(string? sessionId = null, long seqBase = 0,
        System.Text.Json.Nodes.JsonObject? replica = null)
    {
        var session = new ClientSession(_sendTo, _options,
            sessionId: sessionId, seqBase: seqBase, replica: replica, sendFrameTo: _sendFrameTo);
        return session;
    }

    /// <summary>
    /// Resume a persisted session: schema gate → optional migrator → codec
    /// decode. The VM type comes from the record, so TotalSerializer can own
    /// construction; drift without a migrator or an unreadable format → null
    /// (fresh session) — never silently decode a drifted/foreign record (§5.4).
    /// Client-authoritative records replay the stored mirror as-is (§4.9).
    /// </summary>
    private async Task<SessionEndpointBase?> TryResumeAsync(string? sessionId)
    {
        if (_options.Provider is null || sessionId is null) return null;
        var record = await _options.Provider.LoadAsync(sessionId).ConfigureAwait(false);
        if (record?.State is null) return null;

        // Client-authoritative record: the mirror comes back as-is; the
        // authority reattaches upstream at its own pace (§4.9).
        if (record.VmType == "client")
        {
            return record.State is System.Text.Json.Nodes.JsonObject replica
                ? CreateClientSession(record.SessionId, record.LastSeq, replica)
                : null;
        }

        var vmType = Type.GetType(record.VmType);
        if (vmType is null || !typeof(ApplicationModel).IsAssignableFrom(vmType))
        {
            _options.Logger?.LogWarning(
                "Session {SessionId}: record VM type '{VmType}' not resolvable — fresh session",
                sessionId, record.VmType);
            return null;
        }
        var model = VmModel.For(vmType);

        var state = record.State;
        if (record.Schema is not null && record.Schema != model.SchemaFingerprint)
        {
            var migrator = _options.Migrators
                .FirstOrDefault(m => m.CanMigrate(record.Schema, model.SchemaFingerprint));
            if (migrator is null)
            {
                _options.Logger?.LogWarning(
                    "Session {SessionId}: schema drift and no migrator — fresh session", sessionId);
                return null;
            }
            state = migrator.Migrate(record.State, record.Schema, model.SchemaFingerprint);
            if (state is null) return null;
        }

        var codec = _options.Codec ?? SessionStateCodecJson.Instance;
        var format = record.StateFormat ?? SessionStateCodecJson.FormatName;
        if (!codec.CanRead(format))
        {
            _options.Logger?.LogWarning(
                "Session {SessionId}: codec '{Codec}' cannot read record format '{Format}' — fresh session",
                sessionId, codec.Format, format);
            return null;
        }

        ApplicationModel? vm;
        try { vm = codec.DeserializeState(state, format, _vmFactory); }
        catch (Exception ex)
        {
            _options.Logger?.LogError(ex,
                "Session {SessionId}: record decode failed — fresh session", sessionId);
            return null;
        }
        if (vm is null) return null;

        return CreateSession(vm, sessionId: record.SessionId, seqBase: record.LastSeq);
    }

    private Session CreateSession(ApplicationModel vm, string? sessionId = null, long seqBase = 0)
    {
        var session = new Session(vm, _sendTo, _options,
            sessionId: sessionId, seqBase: seqBase, sendFrameTo: _sendFrameTo);
        WireSharedMembers(session);
        _options.ConfigureSession?.Invoke(session);
        return session;
    }

    /// <summary>Resolve [Shared] members: assign the registry instance and
    /// subscribe this session to its (path-rebased) patch stream.</summary>
    private void WireSharedMembers(Session session)
    {
        var model = MemberModel.For(session.Root.GetType(), includeServerOnly: true);
        foreach (var (wire, prop) in model.Properties)
        {
            if (!prop.Shared) continue;
            var shared = SharedObjects.Get(prop.SharedKey!);
            if (shared is null || !prop.Info.PropertyType.IsInstanceOfType(shared.Root))
            {
                _options.Logger?.LogWarning(
                    "Session {SessionId}: [Shared] member '{Wire}' — no compatible shared object for key '{Key}'",
                    session.Id, wire, prop.SharedKey);
                continue;
            }
            if (prop.Writer is not null)
                prop.Write(session.Root, shared.Root);
            session.TrackShared(shared, "/" + JsonPointer.Escape(wire));
        }
    }

    /// <summary>Route any post-hello envelope to the session owning this connection —
    /// the endpoint decides handling by connection role (authority upstream vs client).</summary>
    public void HandleInbound(string connectionId, RamphastosMessage msg)
    {
        if (SessionFor(connectionId) is { } session)
            session.PostInbound(connectionId, msg);
    }

    /// <summary>Inbound binary frame: [flags u8][streamId u32][seq u32][payload].</summary>
    public void HandleFrame(string connectionId, ReadOnlySpan<byte> frame)
    {
        if (SessionFor(connectionId) is not { } session) return;
        if (!StreamFrame.TryDecode(frame, out var streamId, out var seq, out var payload))
            return; // malformed frame — drop silently (no session context worth logging to client)
        session.PostFrameInbound(connectionId, streamId, seq, payload.ToArray());
    }

    private SessionEndpointBase? SessionFor(string connectionId) =>
        _connectionSessions.TryGetValue(connectionId, out var sessionId) &&
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : null;

    /// <summary>Detach a connection (socket closed); schedule suspend when it was the last one.</summary>
    public void HandleDisconnect(string connectionId)
    {
        if (_connectionSessions.TryRemove(connectionId, out var sessionId) &&
            _sessions.TryGetValue(sessionId, out var session))
        {
            session.Disconnect(connectionId);
            if (_options.Provider is not null)
                _ = SuspendAfterGraceAsync(session);
        }
    }

    private async Task SuspendAfterGraceAsync(SessionEndpointBase session)
    {
        await Task.Delay(_options.DisconnectGrace).ConfigureAwait(false);
        if (session.ConnectionCount > 0 || !_sessions.ContainsKey(session.Id))
            return;

        // Serialize inside the actor turn for a consistent graph snapshot.
        await session.SuspendAsync().ConfigureAwait(false);
        if (_sessions.TryRemove(session.Id, out _))
            await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
        _connectionSessions.Clear();
        await SharedObjects.DisposeAsync().ConfigureAwait(false);
    }
}
