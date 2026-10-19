using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Astropiano.Ramphastos.Core;
using Astropiano.Ramphastos.Protocol;
using Astropiano.Ramphastos.TestSdk;
using Xunit;

namespace Astropiano.Ramphastos.Core.Facts;

/// <summary>
/// Client-authoritative sessions (§4.9): the authority pushes snapshot/patch
/// upstream, the server mirrors into a replica, replays to viewers and
/// tunnels invokes/streams.
/// </summary>
public class ClientSessionFacts
{
    private static readonly Encoding Utf8 = Encoding.UTF8;
    private static async Task FlushAsync() => await Task.Delay(80);

    private static JsonObject State(params (string key, JsonNode? value)[] props)
    {
        var o = new JsonObject();
        foreach (var (k, v) in props) o[k] = v;
        return o;
    }

    [Fact]
    public async Task Authority_attaches_and_pushes_snapshot_for_viewers()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        authority.Role.Should().Be("authority");

        host.PushSnapshot(authority, State(("title", JsonValue.Create("Desktop A"))));
        await FlushAsync();

        var viewer = await host.ConnectAsync(role: "viewer", sessionId: authority.SessionId);
        viewer.At("/title")!.GetValue<string>().Should().Be("Desktop A");
    }

    [Fact]
    public async Task Viewer_before_authority_gets_empty_state_then_catches_up()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var viewer = await host.ConnectAsync(sessionId: authority.SessionId);
        viewer.State.Should().BeEmpty("no state pushed yet");

        host.PushSnapshot(authority, State(("count", JsonValue.Create(3))));
        await FlushAsync();

        viewer.At("/count")!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task Authority_patches_fan_out_to_all_viewers_and_replica()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        host.PushSnapshot(authority,
            State(("title", JsonValue.Create("old")), ("items", new JsonArray(1, 2))));
        await FlushAsync();
        var v1 = await host.ConnectAsync(sessionId: authority.SessionId);
        var v2 = await host.ConnectAsync(sessionId: authority.SessionId);

        host.PushPatch(authority,
            new PatchOp { Op = PatchOp.Replace, Path = "/title", Value = JsonValue.Create("new") },
            new PatchOp { Op = PatchOp.Add, Path = "/items/-", Value = JsonValue.Create(3) });
        await FlushAsync();

        foreach (var v in new[] { v1, v2 })
        {
            v.At("/title")!.GetValue<string>().Should().Be("new");
            v.At("/items/2")!.GetValue<int>().Should().Be(3);
        }

        // a late joiner gets the patched replica in its snapshot
        var late = await host.ConnectAsync(sessionId: authority.SessionId);
        late.At("/title")!.GetValue<string>().Should().Be("new");
        late.At("/items")!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public async Task Controller_invoke_forwards_to_authority_and_result_routes_back()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var c = await host.ConnectAsync(sessionId: authority.SessionId);

        var forwarded = authority.ExpectInvoke();
        var resultTask = host.InvokeAsync(c, "/vm/rename", "lobby");
        var invoke = await forwarded;

        invoke.Path.Should().Be("/vm/rename");
        invoke.Args![0]!.GetValue<string>().Should().Be("lobby");

        host.SendResult(authority, invoke.Id, JsonValue.Create("done"));
        (await resultTask)!.GetValue<string>().Should().Be("done");
    }

    [Fact]
    public async Task Authority_errors_route_back_to_the_invoker()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var c = await host.ConnectAsync(sessionId: authority.SessionId);

        var forwarded = authority.ExpectInvoke();
        var resultTask = host.InvokeAsync(c, "/vm/boom");
        var invoke = await forwarded;
        host.SendResult(authority, invoke.Id,
            error: new ErrorBody { Code = "invoke_failed", Message = "nope" });

        var act = async () => await resultTask;
        await act.Should().ThrowAsync<RamphastosTestException>()
            .Where(e => e.Error.Code == "invoke_failed");
    }

    [Fact]
    public async Task Viewer_invoke_is_forbidden_on_mirrored_sessions()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var viewer = await host.ConnectAsync(role: "viewer", sessionId: authority.SessionId);

        var act = async () => await host.InvokeAsync(viewer, "/vm/rename", "x");
        await act.Should().ThrowAsync<RamphastosTestException>()
            .Where(e => e.Error.Code == "forbidden");
        authority.Invokes.Should().BeEmpty("nothing reaches the authority");
    }

    [Fact]
    public async Task Invoke_while_authority_offline_fails_fast()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var c = await host.ConnectAsync(sessionId: authority.SessionId);
        host.Disconnect(authority);
        await FlushAsync();

        var act = async () => await host.InvokeAsync(c, "/vm/rename", "x");
        await act.Should().ThrowAsync<RamphastosTestException>()
            .Where(e => e.Error.Code == "authority_offline");
    }

    [Fact]
    public async Task Replica_survives_authority_disconnect_for_viewers()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        host.PushSnapshot(authority, State(("title", JsonValue.Create("persisted"))));
        await FlushAsync();
        host.Disconnect(authority);
        await FlushAsync();

        // viewers keep last known state; late joiners still get the replica
        var viewer = await host.ConnectAsync(sessionId: authority.SessionId);
        viewer.At("/title")!.GetValue<string>().Should().Be("persisted");
    }

    [Fact]
    public async Task Authority_reconnects_with_same_session_id()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var sessionId = authority.SessionId!;
        host.PushSnapshot(authority, State(("v", JsonValue.Create(1))));
        await FlushAsync();
        host.Disconnect(authority);
        await FlushAsync();

        var authority2 = await host.ConnectAuthorityAsync(sessionId);
        authority2.SessionId.Should().Be(sessionId);

        // viewers never lost the replica; new pushes still fan out
        host.PushPatch(authority2,
            new PatchOp { Op = PatchOp.Replace, Path = "/v", Value = JsonValue.Create(2) });
        await FlushAsync();
        var viewer = await host.ConnectAsync(sessionId: sessionId);
        viewer.At("/v")!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Authority_cannot_claim_a_server_owned_session()
    {
        await using var host = new TestHost(() => new DemoVm());
        var client = await host.ConnectAsync();

        var impostor = await host.ConnectAuthorityAsync(client.SessionId);
        impostor.FatalError.Should().NotBeNull();
        impostor.FatalError!.Code.Should().Be("forbidden");
    }

    [Fact]
    public async Task Bad_patch_op_is_dropped_without_corrupting_the_replica()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        host.PushSnapshot(authority, State(("a", JsonValue.Create(1))));
        await FlushAsync();
        var viewer = await host.ConnectAsync(sessionId: authority.SessionId);

        host.PushPatch(authority,
            new PatchOp { Op = PatchOp.Replace, Path = "/missing/deep", Value = JsonValue.Create(9) },
            new PatchOp { Op = PatchOp.Replace, Path = "/a", Value = JsonValue.Create(2) });
        await FlushAsync();

        authority.Errors.Should().Contain(e => e.Code == "session_corrupted");
        viewer.At("/a")!.GetValue<int>().Should().Be(2, "the valid op still applies");
    }

    [Fact]
    public async Task Stream_request_tunnels_to_authority_and_frames_relay()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var c = await host.ConnectAsync(sessionId: authority.SessionId);
        var viewer = await host.ConnectAsync(role: "viewer", sessionId: authority.SessionId);

        // client requests a PTY stream → forwarded to the authority
        var req = authority.ExpectStreamRequest();
        var openTask = host.OpenStreamAsync(c, "pty", StreamDirection.Duplex);
        var forwarded = await req;
        forwarded.Stream.Should().Be("pty");

        host.AuthorityOpenStream(authority, forwarded.Stream, forwarded.Direction);
        var stream = await openTask;
        viewer.Streams.Should().ContainKey(stream.StreamId, "announcement reaches peers");

        // upstream: client keystrokes → authority
        host.WriteStream(c, stream, Utf8.GetBytes("ls -la\n"));
        await FlushAsync();
        authority.Streams[stream.StreamId].Frames
            .Select(f => Utf8.GetString(f.Payload)).Should().Equal("ls -la\n");

        // downstream: authority output → every client
        host.WriteStream(authority, authority.Streams[stream.StreamId], Utf8.GetBytes("total 0\n"));
        await FlushAsync();
        c.Streams[stream.StreamId].Frames.Select(f => Utf8.GetString(f.Payload))
            .Should().Equal("total 0\n");
        viewer.Streams[stream.StreamId].Frames.Select(f => Utf8.GetString(f.Payload))
            .Should().Equal("total 0\n"); // viewers receive stream output read-only
    }

    [Fact]
    public async Task Authority_can_deny_a_stream_request()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var c = await host.ConnectAsync(sessionId: authority.SessionId);

        var req = authority.ExpectStreamRequest();
        var openTask = host.OpenStreamAsync(c, "pty");
        var forwarded = await req;
        host.Manager.HandleInbound(authority.ConnectionId, new ErrorMessage
        {
            Error = new ErrorBody
            {
                Code = "stream_rejected", Message = "no such pty", Stream = forwarded.Stream,
            },
        });

        var act = async () => await openTask;
        await act.Should().ThrowAsync<RamphastosTestException>()
            .Where(e => e.Error.Code == "stream_rejected");
    }

    [Fact]
    public async Task Authority_disconnect_kills_streams_and_fails_pending_invokes()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var c = await host.ConnectAsync(sessionId: authority.SessionId);

        var req = authority.ExpectStreamRequest();
        var openTask = host.OpenStreamAsync(c, "pty", StreamDirection.Duplex);
        host.AuthorityOpenStream(authority, (await req).Stream, StreamDirection.Duplex);
        var stream = await openTask;

        var pending = authority.ExpectInvoke();
        var invokeTask = host.InvokeAsync(c, "/vm/slow");
        await pending; // invoke is in flight

        host.Disconnect(authority);
        await FlushAsync();

        var act = async () => await invokeTask;
        await act.Should().ThrowAsync<RamphastosTestException>()
            .Where(e => e.Error.Code == "authority_offline");
        c.Streams.Should().NotContainKey(stream.StreamId, "the stream died with the authority");
    }

    [Fact]
    public async Task Mirror_persists_and_resumes_across_suspend()
    {
        var provider = new SessionProviderInMemory();
        await using var host = new TestHost(() => new DemoVm(), new SessionOptions
        {
            Provider = provider,
            DisconnectGrace = TimeSpan.FromMilliseconds(50),
        });
        var authority = await host.ConnectAuthorityAsync();
        var sessionId = authority.SessionId!;
        host.PushSnapshot(authority, State(("title", JsonValue.Create("saved"))));
        await FlushAsync();

        host.Disconnect(authority);
        await Task.Delay(250); // past the grace window → suspended + removed

        var authority2 = await host.ConnectAuthorityAsync(sessionId);
        var viewer = await host.ConnectAsync(sessionId: sessionId);
        viewer.At("/title")!.GetValue<string>().Should().Be("saved",
            "the replica was restored from the provider");
    }

    [Fact]
    public async Task Viewer_pushing_state_is_forbidden()
    {
        await using var host = new TestHost(() => new DemoVm());
        var authority = await host.ConnectAuthorityAsync();
        var viewer = await host.ConnectAsync(role: "viewer", sessionId: authority.SessionId);

        host.Manager.HandleInbound(viewer.ConnectionId, new PatchMessage
        {
            Ops = [new PatchOp { Op = PatchOp.Add, Path = "/hax", Value = JsonValue.Create(1) }],
        });
        await FlushAsync();

        viewer.Errors.Should().Contain(e => e.Code == "forbidden");
        var session = (ClientSession)host.Manager.Endpoints.Single();
        session.Replica.Should().NotContainKey("hax");
    }
}
