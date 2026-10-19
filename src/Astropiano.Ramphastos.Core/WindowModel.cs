namespace Astropiano.Ramphastos.Core;

/// <summary>
/// How a window is presented. Host-agnostic data — a browser tab, an in-page
/// window, a Tauri/CEF/OS window each render it their way.
/// Serialized as strings on the wire ("maximized").
/// </summary>
public enum WindowState
{
    Normal,
    Minimized,
    Maximized,
    Fullscreen,
}

/// <summary>
/// A window — the middle layer of the
/// ApplicationModel → WindowModel → content split:
///
///   the session root (<see cref="ApplicationModel"/>) is the whole-app state
///   (the Windows collection, focus tracking); a <see cref="WindowModel"/> is
///   one window's state — how the host realizes it (browser tab, in-page
///   window, Tauri/CEF/OS window) is not the model's business;
///   <see cref="Content"/> is the synced object currently hosted in that
///   window — swappable, which is navigation (the old
///   RoutingUrl/RootViewModel swap) while the window persists.
///
/// Apps subclass and add window-specific state; content VMs hold the
/// document/edit state a window doesn't need — and vice versa.
///
/// Close flow lives in the app: a close command looks the window up, calls
/// <see cref="OnClosing"/> (veto — e.g. unsaved document), removes it from the
/// collection, then calls <see cref="OnClosed"/>. Physical teardown (tab
/// close) is covered by <see cref="ApplicationModel.OnClientDisconnected"/>
/// for the connection named in <see cref="OwnerConnection"/>.
/// </summary>
public class WindowModel : SyncedObject
{
    private string _id = "";
    /// <summary>App-level window key — what hello meta / commands refer to.</summary>
    public string Id
    {
        get => _id;
        set => Set(ref _id, value);
    }

    private string _title = "";
    /// <summary>The window's title — OS titlebar, tab label, in-page caption.</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    private string _icon = "";
    /// <summary>Icon key or URL — the host resolves it (asset map, data URI, file).</summary>
    public string Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    private WindowState _state = WindowState.Normal;
    public WindowState State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    private bool _hasFocus;
    /// <summary>Whether this window holds the app focus.</summary>
    public bool HasFocus
    {
        get => _hasFocus;
        set => Set(ref _hasFocus, value);
    }

    private string? _ownerConnection;
    /// <summary>The connection that owns this window — the tab/display showing it.</summary>
    public string? OwnerConnection
    {
        get => _ownerConnection;
        set => Set(ref _ownerConnection, value);
    }

    private SyncedObject? _content;
    /// <summary>
    /// The view-model hosted in this window — replaceable without touching the
    /// chrome. Swapping <see cref="Content"/> is navigation.
    /// </summary>
    public SyncedObject? Content
    {
        get => _content;
        set => Set(ref _content, value);
    }

    /// <summary>Close requested (window X, close command). Return false to veto.</summary>
    public virtual bool OnClosing() => true;

    /// <summary>After the window closed and left the graph.</summary>
    public virtual void OnClosed() { }
}
