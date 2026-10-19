using System.Text;
using FluentAssertions;
using Astropiano.Ramphastos.Core;
using Astropiano.Ramphastos.Protocol;
using Astropiano.Ramphastos.TestSdk;
using Xunit;

namespace Astropiano.Ramphastos.Core.Facts;

public class StreamFacts
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    /// <summary>A session that declares a bidirectional "echo" stream.</summary>
    private static SessionOptions EchoOptions(List<byte[]>? sink = null) => new()
    {
        ConfigureSession = session => session.DeclareStream(new StreamDeclaration
        {
            Name = "echo",
            Direction = StreamDirection.Duplex,
            OnData = data =>
            {
                sink?.Add(data.ToArray());
                return ValueTask.CompletedTask;
            },
            OnClose = reason =>
            {
                sink?.Add(Utf8.GetBytes($"__closed:{reason}"));
                return ValueTask.CompletedTask;
            },
        }),
    };

    private static async Task FlushAsync() => await Task.Delay(80);

    [Fact]
    public async Task Client_opens_declared_stream_and_receives_id()
    {
        await using var host = new TestHost(() => new DemoVm(), EchoOptions());
        var c = await host.ConnectAsync();

        var stream = await host.OpenStreamAsync(c, "echo");

        stream.StreamId.Should().BeGreaterThan(0);
        stream.Direction.Should().Be(StreamDirection.Duplex);
    }

    [Fact]
    public async Task Server_writes_flow_downstream_to_all_connections()
    {
        await using var host = new TestHost(() => new DemoVm(), EchoOptions());
        var c = await host.ConnectAsync();
        var viewer = await host.ConnectAsync(role: "viewer", sessionId: c.SessionId);
        var stream = await host.OpenStreamAsync(c, "echo");

        // viewer learns about the stream via the announcement
        viewer.Streams.Should().ContainKey(stream.StreamId);

        // server-side open of the same declaration → second stream instance
        var session = host.Manager.Sessions.Single();
        var serverOpened = await session.OpenStreamAsync("echo");
        serverOpened!.Write(Utf8.GetBytes("hello clients"));
        await FlushAsync();

        viewer.Streams[serverOpened.Id].Frames.Select(f => Utf8.GetString(f.Payload))
            .Should().Equal("hello clients");
        c.Streams[serverOpened.Id].Frames.Select(f => Utf8.GetString(f.Payload))
            .Should().Equal("hello clients");
    }

    [Fact]
    public async Task Upstream_frames_reach_the_declaration_sink_in_order()
    {
        var sink = new List<byte[]>();
        await using var host = new TestHost(() => new DemoVm(), EchoOptions(sink));
        var c = await host.ConnectAsync();
        var stream = await host.OpenStreamAsync(c, "echo", StreamDirection.Up);

        host.WriteStream(c, stream, Utf8.GetBytes("first"));
        host.WriteStream(c, stream, Utf8.GetBytes("second"));
        await FlushAsync();

        sink.Select(Utf8.GetString).Should().Equal("first", "second");
    }

    [Fact]
    public async Task Viewer_cannot_open_streams()
    {
        await using var host = new TestHost(() => new DemoVm(), EchoOptions());
        var controller = await host.ConnectAsync();
        var viewer = await host.ConnectAsync(role: "viewer", sessionId: controller.SessionId);

        var act = () => host.OpenStreamAsync(viewer, "echo");
        await act.Should().ThrowAsync<RamphastosTestException>()
            .Where(e => e.Error.Code == "forbidden");
    }

    [Fact]
    public async Task Undeclared_stream_is_rejected()
    {
        await using var host = new TestHost(() => new DemoVm(), EchoOptions());
        var c = await host.ConnectAsync();

        var act = () => host.OpenStreamAsync(c, "nope");
        await act.Should().ThrowAsync<RamphastosTestException>()
            .Where(e => e.Error.Code == "unknown_stream");
    }

    [Fact]
    public async Task Upstream_data_on_down_only_stream_is_forbidden()
    {
        await using var host = new TestHost(() => new DemoVm(), new SessionOptions
        {
            ConfigureSession = s => s.DeclareStream(new StreamDeclaration
            {
                Name = "out", Direction = StreamDirection.Down,
            }),
        });
        var c = await host.ConnectAsync();
        var stream = await host.OpenStreamAsync(c, "out");

        host.WriteStream(c, stream, Utf8.GetBytes("should-not-land"));
        await FlushAsync();

        c.Errors.Should().Contain(e => e.Code == "forbidden" && e.Stream == "out");
    }

    [Fact]
    public async Task Stream_close_propagates()
    {
        await using var host = new TestHost(() => new DemoVm(), EchoOptions());
        var c = await host.ConnectAsync();
        var other = await host.ConnectAsync(sessionId: c.SessionId);
        var stream = await host.OpenStreamAsync(c, "echo");

        host.CloseStream(c, stream, "done");
        await FlushAsync();

        c.Streams.Should().NotContainKey(stream.StreamId);
        other.Streams.Should().NotContainKey(stream.StreamId,
            "peers see the close too");
    }
}
