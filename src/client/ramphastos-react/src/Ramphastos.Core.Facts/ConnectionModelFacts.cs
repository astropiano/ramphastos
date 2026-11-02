using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using FluentAssertions;
using Ramphastos.Core;
using Ramphastos.TestSdk;
using Xunit;

namespace Ramphastos.Core.Facts;

/// <summary>Content view-model hosted inside a window — its state is the
/// window's business, not the chrome's.</summary>
public class DocVm : SyncedObject
{
    private string _text = "";
    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    private bool _dirty;
    /// <summary>Unsaved-document flag — consulted by the window's close veto.</summary>
    public bool Dirty
    {
        get => _dirty;
        set => Set(ref _dirty, value);
    }
}

/// <summary>App window: framework window model + swappable content.</summary>
public class DocWindowModel : WindowModel
{
    public override bool OnClosing() => Content is not DocVm doc || !doc.Dirty;
}

/// <summary>
/// App-level window model on the new primitives: the hello meta carries the
/// window id, the connect/disconnect hooks maintain a synced Windows list,
/// and commands read the invoking connection via ConnectionContext.
/// </summary>
public class DesktopApp : ApplicationModel
{
    public ObservableCollection<DocWindowModel> Windows { get; } = new();

    private string? _lastFocusedWindowId;
    public string? LastFocusedWindowId
    {
        get => _lastFocusedWindowId;
        set => Set(ref _lastFocusedWindowId, value);
    }

    public override void OnClientConnected(SessionConnection connection)
    {
        var windowId = (string?)connection.Meta?["window"];
        if (windowId is not null)
            Windows.Add(new DocWindowModel
            {
                Id = windowId,
                OwnerConnection = connection.Id,
                Content = new DocVm(),
            });
    }

    public override void OnClientDisconnected(SessionConnection connection)
    {
        var w = Windows.FirstOrDefault(x => x.OwnerConnection == connection.Id);
        if (w is not null) Windows.Remove(w);
    }

    [Command]
    public Task<string?> MyWindow() =>
        Task.FromResult((string?)ConnectionContext.Current?.Meta?["window"]);

    [Command]
    public Task RenameWindow(string title)
    {
        var w = Windows.FirstOrDefault(x => x.OwnerConnection == ConnectionContext.Current?.Id);
        if (w is not null) w.Title = title;
        return Task.CompletedTask;
    }

    /// <summary>In-page focus: mark the window, track last-focused app-wide.</summary>
    [Command]
    public Task FocusWindow(string id)
    {
        foreach (var w in Windows) w.HasFocus = false;
        var target = Windows.FirstOrDefault(x => x.Id == id);
        if (target is not null)
        {
            target.HasFocus = true;
            LastFocusedWindowId = id;
        }
        return Task.CompletedTask;
    }

    /// <summary>Navigation = swapping the hosted content; chrome persists.</summary>
    [Command]
    public Task NavigateWindow(string id)
    {
        var w = Windows.FirstOrDefault(x => x.Id == id);
        if (w is not null) w.Content = new DocVm { Text = "new document" };
        return Task.CompletedTask;
    }

    /// <summary>The in-page close button: veto via OnClosing, then remove.</summary>
    [Command]
    public Task<bool> CloseWindow(string id)
    {
        var w = Windows.FirstOrDefault(x => x.Id == id);
        if (w is null || !w.OnClosing()) return Task.FromResult(false);
        Windows.Remove(w);
        w.OnClosed();
        return Task.FromResult(true);
    }
}

public class ConnectionModelFacts
{
    private static async Task FlushAsync() => await Task.Delay(80);

    private static JsonObject Window(string id) => new() { ["window"] = id };

    [Fact]
    public async Task Hello_meta_reaches_the_connection_hook()
    {
        await using var host = new TestHost(() => new DesktopApp());
        var c = await host.ConnectAsync(meta: Window("w1"));
        await FlushAsync();

        var vm = (DesktopApp)host.Manager.Sessions.Single().Root;
        vm.Windows.Should().ContainSingle();
        vm.Windows[0].Id.Should().Be("w1");
        vm.Windows[0].OwnerConnection.Should().Be(c.ConnectionId);
    }

    [Fact]
    public async Task Windows_sync_to_every_tab_and_disconnect_removes_them()
    {
        await using var host = new TestHost(() => new DesktopApp());
        var a = await host.ConnectAsync(meta: Window("w1"));
        var b = await host.ConnectAsync(sessionId: a.SessionId, meta: Window("w2"));
        await FlushAsync();

        ((JsonArray)a.At("/windows")!).Count.Should().Be(2);
        ((JsonArray)b.At("/windows")!).Count.Should().Be(2);
        a.At("/windows/1/id")!.GetValue<string>().Should().Be("w2");

        host.Disconnect(a);
        await FlushAsync();

        ((JsonArray)b.At("/windows")!).Count.Should().Be(1);
        b.At("/windows/0/id")!.GetValue<string>().Should().Be("w2",
            "w1 closed — the surviving window shifts into index 0");
    }

    [Fact]
    public async Task Invoke_context_exposes_the_calling_connection()
    {
        await using var host = new TestHost(() => new DesktopApp());
        var a = await host.ConnectAsync(meta: Window("w1"));
        var b = await host.ConnectAsync(sessionId: a.SessionId, meta: Window("w2"));
        await FlushAsync();

        (await host.CommandAsync(a, "myWindow"))!.GetValue<string>().Should().Be("w1");
        (await host.CommandAsync(b, "myWindow"))!.GetValue<string>().Should().Be("w2");
    }

    [Fact]
    public async Task Command_can_target_the_callers_window()
    {
        await using var host = new TestHost(() => new DesktopApp());
        var a = await host.ConnectAsync(meta: Window("w1"));
        var b = await host.ConnectAsync(sessionId: a.SessionId, meta: Window("w2"));
        await FlushAsync();

        await host.CommandAsync(b, "renameWindow", "doc editor");
        await FlushAsync();

        a.At("/windows/1/title")!.GetValue<string>().Should().Be("doc editor");
        a.At("/windows/0/title")!.GetValue<string>().Should().Be("", "w1 was not the caller");
    }

    [Fact]
    public async Task Window_presentation_state_syncs_as_strings()
    {
        await using var host = new TestHost(() => new DesktopApp());
        var a = await host.ConnectAsync(meta: Window("w1"));
        await FlushAsync();

        var vm = (DesktopApp)host.Manager.Sessions.Single().Root;
        vm.Windows[0].Icon = "doc";
        vm.Windows[0].State = WindowState.Maximized;
        await FlushAsync();

        a.At("/windows/0/icon")!.GetValue<string>().Should().Be("doc");
        a.At("/windows/0/state")!.GetValue<string>().Should().Be("maximized",
            "enums ride the wire as names, not numbers");
    }

    [Fact]
    public async Task CloseWindow_vetoes_dirty_and_closes_clean()
    {
        await using var host = new TestHost(() => new DesktopApp());
        var a = await host.ConnectAsync(meta: Window("w1"));
        var b = await host.ConnectAsync(sessionId: a.SessionId, meta: Window("w2"));
        await FlushAsync();

        var vm = (DesktopApp)host.Manager.Sessions.Single().Root;
        ((DocVm)vm.Windows[0].Content!).Dirty = true;

        (await host.CommandAsync(a, "closeWindow", "w1"))!.GetValue<bool>()
            .Should().BeFalse("OnClosing vetoed — unsaved document");
        ((JsonArray)a.At("/windows")!).Count.Should().Be(2);

        (await host.CommandAsync(a, "closeWindow", "w2"))!.GetValue<bool>().Should().BeTrue();
        await FlushAsync();
        ((JsonArray)b.At("/windows")!).Count.Should().Be(1);
        b.At("/windows/0/id")!.GetValue<string>().Should().Be("w1");
    }

    [Fact]
    public async Task Focus_and_navigation_sync_window_and_content()
    {
        await using var host = new TestHost(() => new DesktopApp());
        var a = await host.ConnectAsync(meta: Window("w1"));
        await FlushAsync();

        await host.CommandAsync(a, "focusWindow", "w1");
        await FlushAsync();
        a.At("/windows/0/hasFocus")!.GetValue<bool>().Should().BeTrue();
        a.At("/lastFocusedWindowId")!.GetValue<string>().Should().Be("w1");

        await host.CommandAsync(a, "navigateWindow", "w1");
        await FlushAsync();
        a.At("/windows/0/content/text")!.GetValue<string>().Should().Be("new document",
            "swapping Content is navigation — the window chrome persists");
    }
}
