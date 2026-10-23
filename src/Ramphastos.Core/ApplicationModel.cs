namespace Ramphastos.Core;

/// <summary>
/// The application model — the root of a session's synced graph (whole-app
/// state). Root-only concern lives here: the connection lifecycle hooks the
/// session actor calls on the root. Everything below it is plain
/// <see cref="SyncedObject"/> — content view-models, window chrome, models.
/// </summary>
public abstract class ApplicationModel : SyncedObject
{
    /// <summary>
    /// A client attached (controller or viewer). Runs inside the session actor,
    /// after the joiner's snapshot/replay — mutations arrive at the joiner as
    /// ordinary patch ops, so a per-connection window entity can just be added
    /// here. Use <see cref="SessionConnection.Meta"/> for the identity the
    /// client announced at hello.
    /// </summary>
    public virtual void OnClientConnected(SessionConnection connection) { }

    /// <summary>
    /// A client detached (socket closed). Runs inside the session actor —
    /// the place to remove the window entity the connection owned.
    /// </summary>
    public virtual void OnClientDisconnected(SessionConnection connection) { }
}
