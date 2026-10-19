using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astropiano.Ramphastos.Core;
using Astropiano.Ramphastos.Protocol;

namespace Astropiano.Ramphastos.TestSdk;

/// <summary>
/// In-process Ramphastos host: wraps a <see cref="RamphastosSessionManager"/>
/// with a loopback transport. Messages cross the wire format both ways —
/// envelopes are serialized to JsonObject and back, so tests exercise the
/// same contract a real client sees.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TestClient> _clients = new();
    private int _connCounter;

    public RamphastosSessionManager Manager { get; }

    public TestHost(Func<ApplicationModel> vmFactory, SessionOptions? options = null)
    {
        Manager = new RamphastosSessionManager(vmFactory, SendTo, options, SendFrameTo);
    }

    public TestClient? Client(string connectionId) =>
        _clients.TryGetValue(connectionId, out var c) ? c : null;

    public IReadOnlyCollection<TestClient> Clients => _clients.Values.ToList();

    /// <summary>Connect a client; resolves once welcome + snapshot/replay have landed.</summary>
    public async Task<TestClient> ConnectAsync(
        string role = SessionRole.Controller, string? sessionId = null, long? lastSeq = null,
        JsonObject? meta = null)
    {
        var client = new TestClient($"conn_{++_connCounter}");
        _clients[client.ConnectionId] = client;

        await Manager.HandleHello(client.ConnectionId, new HelloMessage
        {
            Role = role,
            Session = sessionId,
            LastSeq = lastSeq,
            Meta = meta,
        }).ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Re-hello an existing client (same connection id, keeping its mirror)
    /// with its current session + lastSeq — exercises the replay path.
    /// </summary>
    public async Task ReconnectAsync(TestClient client)
    {
        var lastSeq = client.Messages.Count == 0 ? 0 : client.Messages.Max(m => m.Seq);
        await Manager.HandleHello(client.ConnectionId, new HelloMessage
        {
            Role = client.Role ?? SessionRole.Controller,
            Session = client.SessionId,
            LastSeq = lastSeq,
        }).ConfigureAwait(false);
    }

    public void Disconnect(TestClient client) => Manager.HandleDisconnect(client.ConnectionId);

    /// <summary>Send an invoke; resolves with the result value or throws RamphastosTestException.</summary>
    public async Task<JsonNode?> InvokeAsync(TestClient client, string path, params object?[] args)
    {
        var id = $"c-{Guid.NewGuid():N}";
        var expect = client.ExpectResult(id);
        Manager.HandleInbound(client.ConnectionId, new InvokeMessage
        {
            Id = id,
            Path = path,
            Args = args.Length == 0
                ? null
                : new JsonArray(args.Select(a =>
                    a is null ? null : JsonSerializer.SerializeToNode(a, MessageSerializer.Options)).ToArray<JsonNode?>()),
        });
        return await expect.ConfigureAwait(false);
    }

    /// <summary>Command shorthand: invoke "/vm/&lt;name&gt;".</summary>
    public Task<JsonNode?> CommandAsync(TestClient client, string name, params object?[] args) =>
        InvokeAsync(client, "/vm/" + name, args);

    /// <summary>Write intent: invoke "/props/&lt;name&gt;" with one arg.</summary>
    public Task<JsonNode?> WriteAsync(TestClient client, string prop, object? value) =>
        InvokeAsync(client, "/props/" + prop, value);

    /// <summary>Client requests a stream; resolves when stream-opened lands (or throws on error).</summary>
    public async Task<TestStream> OpenStreamAsync(TestClient client, string name,
        string direction = StreamDirection.Down)
    {
        var expect = client.ExpectStreamOpened(name);
        Manager.HandleInbound(client.ConnectionId, new StreamOpenMessage
        {
            Stream = name,
            Direction = direction,
        });
        return await expect.ConfigureAwait(false);
    }

    /// <summary>Write a payload upstream (client → server data plane).</summary>
    public void WriteStream(TestClient client, TestStream stream, byte[] payload)
    {
        var frame = StreamFrame.Encode(stream.StreamId, (uint)stream.NextClientSeq++, payload);
        Manager.HandleFrame(client.ConnectionId, frame);
    }

    /// <summary>Client-side stream close — also marks the local stream closed
    /// (the server does not echo a close back to the closing connection).</summary>
    public void CloseStream(TestClient client, TestStream stream, string? reason = null)
    {
        if (client.Streams.TryRemove(stream.StreamId, out var local))
            local.ClosedReason = reason ?? "";
        Manager.HandleInbound(client.ConnectionId, new StreamCloseMessage
        {
            Stream = stream.Name,
            StreamId = stream.StreamId,
            Reason = reason,
        });
    }

    // ---- authority helpers (§4.9): the authority is a client role that
    // pushes state upstream; the host mirrors and relays it to viewers. ----

    /// <summary>Attach a connection as session authority.</summary>
    public Task<TestClient> ConnectAuthorityAsync(string? sessionId = null) =>
        ConnectAsync(SessionRole.Authority, sessionId);

    /// <summary>Authority pushes a full state snapshot; the host stores the
    /// replica and fans a wrapped snapshot out to all viewers.</summary>
    public void PushSnapshot(TestClient authority, JsonObject state) =>
        Manager.HandleInbound(authority.ConnectionId, new SnapshotMessage { Root = state });

    /// <summary>Authority pushes patch ops (paths relative to its own state root).</summary>
    public void PushPatch(TestClient authority, params PatchOp[] ops) =>
        Manager.HandleInbound(authority.ConnectionId, new PatchMessage { Ops = ops.ToList() });

    /// <summary>Authority replies to a forwarded invoke.</summary>
    public void SendResult(TestClient authority, string refId, JsonNode? value = null,
        ErrorBody? error = null) =>
        Manager.HandleInbound(authority.ConnectionId, new ResultMessage
        {
            Ref = refId,
            Value = value,
            Error = error,
        });

    /// <summary>Authority grants (or opens) a stream — the server assigns the id.</summary>
    public void AuthorityOpenStream(TestClient authority, string name,
        string direction = StreamDirection.Duplex) =>
        Manager.HandleInbound(authority.ConnectionId, new StreamOpenMessage
        {
            Stream = name,
            Direction = direction,
        });

    private Task SendTo(string connectionId, RamphastosMessage message)
    {
        // Cross the wire format: serialize → parse → typed message, so tests
        // observe exactly what a remote client would.
        var envelope = MessageSerializer.Serialize(message);
        var typed = MessageSerializer.Deserialize(envelope);
        if (_clients.TryGetValue(connectionId, out var client))
            client.Deliver(typed);
        return Task.CompletedTask;
    }

    private Task SendFrameTo(string connectionId, byte[] frame)
    {
        if (_clients.TryGetValue(connectionId, out var client))
            client.DeliverFrame(frame);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Manager.DisposeAsync();
}
