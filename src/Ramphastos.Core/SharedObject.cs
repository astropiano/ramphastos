using System.Collections.Concurrent;
using System.Threading.Channels;
using Ramphastos.Protocol;

namespace Ramphastos.Core;

/// <summary>
/// One process-wide shared VM graph (§6.3): its own actor mailbox serializes
/// all mutation, and its tracker emits ops that are rebased into every
/// subscribing session's patch stream at that session's member path.
///
/// Sessions do NOT attach their own trackers to the shared subtree — that
/// keeps single ownership of change notification (no cross-session tracker
/// mutation, no torn reads mid-write). Mirrors converge through spliced ops.
/// Shared state is live: it never enters session records.
/// </summary>
public sealed class SharedObject : IAsyncDisposable
{
    private readonly Channel<Func<Task>> _mailbox = Channel.CreateUnbounded<Func<Task>>();
    private readonly GraphTracker _tracker;
    private readonly Task _runLoop;
    private readonly List<(Session session, string prefix)> _subscribers = new();
    private readonly object _subGate = new();

    internal SharedObject(SyncedObject root)
    {
        Root = root;
        _tracker = new GraphTracker(Fanout);
        _tracker.Attach(root);
        _runLoop = Task.Run(RunAsync);
    }

    /// <summary>The shared view-model instance — same object in every session.</summary>
    public SyncedObject Root { get; }

    /// <summary>Enqueue a mutation on the shared actor. Safe from any thread.</summary>
    public void Post(Action action) =>
        _mailbox.Writer.TryWrite(() => { action(); return Task.CompletedTask; });

    /// <summary>Enqueue async work on the shared actor.</summary>
    public void Post(Func<Task> work) => _mailbox.Writer.TryWrite(work);

    private async Task RunAsync()
    {
        await foreach (var work in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
            await work().ConfigureAwait(false);
    }

    /// <summary>Tracked ops fan out to every subscribing session, rebased to its path.</summary>
    private void Fanout(PatchOp op, bool reduced)
    {
        (Session, string)[] subs;
        lock (_subGate) subs = _subscribers.ToArray();
        foreach (var (session, prefix) in subs)
            session.EmitShared(prefix, op, reduced);
    }

    internal void Subscribe(Session session, string memberPath)
    {
        lock (_subGate)
            if (_subscribers.All(s => s.session != session))
                _subscribers.Add((session, memberPath));
    }

    internal void Unsubscribe(Session session)
    {
        lock (_subGate) _subscribers.RemoveAll(s => s.session == session);
    }

    internal int SubscriberCount
    {
        get { lock (_subGate) return _subscribers.Count; }
    }

    public async ValueTask DisposeAsync()
    {
        _tracker.DetachAll();
        _mailbox.Writer.TryComplete();
        try { await _runLoop.ConfigureAwait(false); }
        catch (ChannelClosedException) { }
    }
}

/// <summary>
/// Registry of SharedObject instances, keyed by name. Sessions resolve their
/// [Shared] members here; the app mutates shared state through
/// <see cref="SharedObject.Post"/>.
/// </summary>
public sealed class SharedObjectRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SharedObject> _objects = new();
    private readonly Dictionary<string, Func<SyncedObject>> _factories;

    public SharedObjectRegistry(
        IReadOnlyDictionary<string, Func<SyncedObject>>? factories = null) =>
        _factories = factories is null
            ? new()
            : new(factories);

    /// <summary>Resolve (creating via the registered factory if needed). Null if unregistered.</summary>
    public SharedObject? Get(string key)
    {
        if (_objects.TryGetValue(key, out var existing)) return existing;
        if (!_factories.TryGetValue(key, out var factory)) return null;
        return _objects.GetOrAdd(key, _ => new SharedObject(factory()));
    }

    /// <summary>Typed lookup by the shared VM's full type name.</summary>
    public SharedObject? Get<T>() where T : SyncedObject => Get(typeof(T).FullName!);

    /// <summary>Register a factory for key (or type full name). Pre-first-get.</summary>
    public void Register<T>(Func<T> factory, string? key = null) where T : SyncedObject
    {
        _factories[key ?? typeof(T).FullName!] = factory;
    }

    /// <summary>Convenience mutation: post work onto a shared object's actor.</summary>
    public void Post<T>(Action<T> work) where T : SyncedObject =>
        Get<T>()?.Post(() => work((T)Get<T>()!.Root));

    public async ValueTask DisposeAsync()
    {
        foreach (var o in _objects.Values)
            await o.DisposeAsync().ConfigureAwait(false);
        _objects.Clear();
    }
}
