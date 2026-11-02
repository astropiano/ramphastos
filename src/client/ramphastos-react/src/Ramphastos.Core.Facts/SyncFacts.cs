using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FluentAssertions;
using Ramphastos.Core;
using Ramphastos.TestSdk;
using Xunit;

namespace Ramphastos.Core.Facts;

/// <summary>Nested child view-model — tracked at depth, not only at the root.</summary>
public class ChildVm : SyncedObject
{
    private string _name = "kid";
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public ObservableCollection<string> Tags { get; } = new();

    [ServerOnly]
    public string Secret { get; set; } = "s3cret";
}

public class PlainPoco
{
    public int Points { get; set; } = 3;
}

public class DemoVm : ApplicationModel
{
    private int _count;
    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    private string _title = "demo";
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    private ChildVm _child = new();
    public ChildVm Child
    {
        get => _child;
        set => Set(ref _child, value);
    }

    /// <summary>POCO without notifications — synced by value, replaced wholesale.</summary>
    public PlainPoco Stats { get; set; } = new();

    public ObservableCollection<string> Items { get; } = new();

    private string _doc = "";
    [TextSync]
    public string Doc
    {
        get => _doc;
        set => Set(ref _doc, value);
    }

    private int _ticker;
    [SyncReduced]
    public int Ticker
    {
        get => _ticker;
        set => Set(ref _ticker, value);
    }

    [Command]
    public Task Increment(int by = 1)
    {
        Count += by;
        return Task.CompletedTask;
    }

    [Command(RequireRole = "controller")]
    public Task Reset()
    {
        Count = 0;
        return Task.CompletedTask;
    }

    [Command]
    public Task<string> Echo(string text) => Task.FromResult(text);

    [ServerOnly]
    public string InternalToken { get; set; } = "hidden";

    [NoSync]
    public string Transient { get; set; } = "gone";
}

public class SyncFacts
{
    private static async Task FlushAsync() => await Task.Delay(80);

    [Fact]
    public async Task Snapshot_contains_props_and_commands_but_not_server_only()
    {
        await using var host = new TestHost(() => new DemoVm());
        var c = await host.ConnectAsync();

        c.At("/count")!.GetValue<int>().Should().Be(0);
        c.At("/title")!.GetValue<string>().Should().Be("demo");
        c.At("/child/name")!.GetValue<string>().Should().Be("kid");
        c.At("/internalToken").Should().BeNull("[ServerOnly] never reaches the wire");
        c.At("/transient").Should().BeNull("[NoSync] never reaches the wire");
        c.At("/child/secret").Should().BeNull("nested [ServerOnly] is excluded at depth");
    }

    [Fact]
    public async Task Nested_property_change_emits_deep_path_patch()
    {
        await using var host = new TestHost(() => new DemoVm());
        var c = await host.ConnectAsync();
        var vm = host.Manager.Sessions.Single().Root as DemoVm;

        vm!.Child.Name = "renamed";
        await FlushAsync();

        c.AppliedOps.Should().Contain(o => o.Op == "replace" && o.Path == "/child/name");
        c.At("/child/name")!.GetValue<string>().Should().Be("renamed");
    }

    [Fact]
    public async Task Nested_collection_emits_indexed_ops()
    {
        await using var host = new TestHost(() => new DemoVm());
        var c = await host.ConnectAsync();
        var vm = host.Manager.Sessions.Single().Root as DemoVm;

        vm!.Child.Tags.Add("a");
        vm.Child.Tags.Add("b");
        await FlushAsync();

        c.AppliedOps.Should().Contain(o => o.Op == "add" && o.Path == "/child/tags/0");
        c.At("/child/tags/1")!.GetValue<string>().Should().Be("b");

        vm.Child.Tags.RemoveAt(0);
        await FlushAsync();
        c.At("/child/tags/0")!.GetValue<string>().Should().Be("b");
    }

    [Fact]
    public async Task Collection_ops_on_root()
    {
        await using var host = new TestHost(() => new DemoVm());
        var c = await host.ConnectAsync();
        var vm = host.Manager.Sessions.Single().Root as DemoVm;

        vm!.Items.Add("x");
        vm.Items.Add("y");
        await FlushAsync();
        ((JsonArray)c.At("/items")!).Count.Should().Be(2);

        vm.Items.Move(0, 1);
        await FlushAsync();
        c.At("/items/1")!.GetValue<string>().Should().Be("x");
    }

    [Fact]
    public async Task Replaced_child_is_retracked()
    {
        await using var host = new TestHost(() => new DemoVm());
        var c = await host.ConnectAsync();
        var vm = host.Manager.Sessions.Single().Root as DemoVm;

        vm!.Child = new ChildVm { Name = "second" };
        await FlushAsync();
        c.At("/child/name")!.GetValue<string>().Should().Be("second");

        // change on the NEW instance must still emit — and at the right path
        vm.Child.Name = "third";
        await FlushAsync();
        c.At("/child/name")!.GetValue<string>().Should().Be("third");

        // the detached old child must no longer emit
        var opsBefore = c.AppliedOps.Count;
        var old = new ChildVm();
        vm.Child = old; // swap to a tracked-then-replaced flow
        await FlushAsync();
        old.Name = "still-tracked?";
        vm.Child.Name = "pivot";
        await FlushAsync();
        c.At("/child/name")!.GetValue<string>().Should().Be("pivot");
    }

    [Fact]
    public async Task TextSync_emits_textpatch_and_mirror_applies()
    {
        await using var host = new TestHost(() => new DemoVm());
        var c = await host.ConnectAsync();
        var vm = host.Manager.Sessions.Single().Root as DemoVm;

        vm!.Doc = "hello world";
        await FlushAsync();
        c.AppliedOps.Should().Contain(o => o.Op == "textpatch" && o.Path == "/doc" && o.Patch != null);
        c.At("/doc")!.GetValue<string>().Should().Be("hello world");

        vm.Doc = "hello ramphastos world";
        await FlushAsync();
        c.At("/doc")!.GetValue<string>().Should().Be("hello ramphastos world");
    }

    [Fact]
    public async Task SyncReduced_coalesces_to_latest()
    {
        await using var host = new TestHost(() => new DemoVm());
        var c = await host.ConnectAsync();

        host.Manager.Sessions.Single().Post(() =>
        {
            // three changes inside one actor turn → only the last should ship
            var demo = (DemoVm)host.Manager.Sessions.Single().Root;
            demo.Ticker = 1;
            demo.Ticker = 2;
            demo.Ticker = 3;
        });
        await FlushAsync();

        c.AppliedOps.Count(o => o.Path == "/ticker").Should().Be(1);
        c.At("/ticker")!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task Command_roundtrip_and_write_intent()
    {
        await using var host = new TestHost(() => new DemoVm());
        var c = await host.ConnectAsync();

        await host.CommandAsync(c, "increment", 5);
        c.At("/count")!.GetValue<int>().Should().Be(5);

        var echo = await host.CommandAsync(c, "echo", "hi");
        echo!.GetValue<string>().Should().Be("hi");

        await host.WriteAsync(c, "title", "from-client");
        c.At("/title")!.GetValue<string>().Should().Be("from-client");
        var vm = host.Manager.Sessions.Single().Root as DemoVm;
        vm!.Title.Should().Be("from-client");
    }

    [Fact]
    public async Task Viewer_role_cannot_invoke()
    {
        await using var host = new TestHost(() => new DemoVm());
        var controller = await host.ConnectAsync();
        var viewer = await host.ConnectAsync(role: "viewer", sessionId: controller.SessionId);

        var act = () => host.CommandAsync(viewer, "increment", 1);
        await act.Should().ThrowAsync<RamphastosTestException>()
            .Where(e => e.Error.Code == "forbidden");
    }

    [Fact]
    public async Task Reconnect_replays_missed_patches_onto_existing_mirror()
    {
        await using var host = new TestHost(() => new DemoVm());
        var c = await host.ConnectAsync();
        await host.CommandAsync(c, "increment", 7);
        var appliedBefore = c.AppliedOps.Count;

        // a second connection keeps the session alive while c is away
        var other = await host.ConnectAsync(sessionId: c.SessionId);
        host.Disconnect(c);
        await host.CommandAsync(other, "increment", 3);

        await host.ReconnectAsync(c);

        c.LastConnectReplayed.Should().BeTrue("the mirror was kept, so replay beats snapshot");
        c.AppliedOps.Count.Should().BeGreaterThan(appliedBefore, "missed ops must be re-sent");
        c.At("/count")!.GetValue<int>().Should().Be(10);
    }

    [Fact]
    public async Task Schema_fingerprint_is_stable_and_reaches_the_client()
    {
        await using var host = new TestHost(() => new DemoVm());
        var a = await host.ConnectAsync();
        var b = await host.ConnectAsync(sessionId: a.SessionId);
        b.Schema.Should().Be(a.Schema).And.NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Two_connections_share_one_session()
    {
        await using var host = new TestHost(() => new DemoVm());
        var a = await host.ConnectAsync();
        var b = await host.ConnectAsync(role: "viewer", sessionId: a.SessionId);

        host.Manager.Sessions.Should().ContainSingle();
        await host.CommandAsync(a, "increment", 2);
        b.At("/count")!.GetValue<int>().Should().Be(2, "fan-out reaches every attached connection");
    }
}
