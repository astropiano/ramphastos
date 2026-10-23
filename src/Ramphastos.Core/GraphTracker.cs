using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Ramphastos.Protocol;

namespace Ramphastos.Core;

/// <summary>
/// Deep-graph change tracking (§6.1): walks the VM object graph once, then
/// subscribes to every INotifyPropertyChanged / INotifyCollectionChanged node
/// it finds — at any depth — and turns their events into RFC 6902 ops against
/// the replicated graph. Plain POCOs are walked through (their own members
/// are leaves, but nested notifying objects inside them are still tracked).
///
/// Object identity is tracked by reference; the first-encountered path owns
/// the subscription for shared nodes (other paths serialize as $ref).
/// </summary>
internal sealed class GraphTracker
{
    private sealed class TrackedNode
    {
        public required object Target { get; init; }
        public required string Path { get; set; }
        public required MemberModel Model { get; init; }
        public List<TrackedNode> Children { get; } = new();
        public PropertyChangedEventHandler? PropHandler { get; set; }
        public NotifyCollectionChangedEventHandler? CollHandler { get; set; }
        /// <summary>Last synced values for [TextSync] members — the diff base.</summary>
        public Dictionary<string, string>? TextValues { get; set; }
    }

    private readonly Dictionary<object, TrackedNode> _nodes = new(ReferenceEqualityComparer.Instance);
    private readonly Action<PatchOp, bool> _emit;

    /// <param name="emit">op sink; second arg marks [SyncReduced] coalescible ops.</param>
    public GraphTracker(Action<PatchOp, bool> emit) => _emit = emit;

    /// <summary>Attach the whole graph rooted at <paramref name="root"/> at path "".</summary>
    public void Attach(object root) => AttachNode(root, "");

    /// <summary>Detach everything (session disposal).</summary>
    public void DetachAll()
    {
        foreach (var node in _nodes.Values.ToList())
            Unsubscribe(node);
        _nodes.Clear();
    }

    private void AttachNode(object target, string path)
    {
        if (target is null || _nodes.ContainsKey(target))
            return; // leaf handled by caller; shared node keeps its first path

        var node = new TrackedNode
        {
            Target = target,
            Path = path,
            Model = MemberModel.For(target.GetType()),
        };
        _nodes[target] = node;

        if (target is INotifyPropertyChanged inpc)
        {
            node.PropHandler = (_, e) => OnPropertyChanged(node, e);
            inpc.PropertyChanged += node.PropHandler;
        }
        if (target is INotifyCollectionChanged incc)
        {
            node.CollHandler = (_, e) => OnCollectionChanged(node, e);
            incc.CollectionChanged += node.CollHandler;
        }

        WalkChildren(node);
    }

    /// <summary>Walk members / items of a node and attach trackable children.</summary>
    private void WalkChildren(TrackedNode node)
    {
        switch (node.Target)
        {
            case IDictionary dict:
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key is string key)
                        TryAttachValue(node, entry.Value, JsonPointer.Join(node.Path, key));
                }
                break;
            case IEnumerable seq when node.Target is not string:
                var i = 0;
                foreach (var item in seq)
                    TryAttachValue(node, item, JsonPointer.Join(node.Path, i++.ToString()));
                break;
            default:
                foreach (var (wire, prop) in node.Model.Properties)
                {
                    // [Shared] subtrees are owned by the SharedObject's own
                    // tracker/actor — attaching here would double-emit and
                    // mutate this tracker from foreign actor threads.
                    if (prop.Shared) continue;
                    object? value;
                    try { value = prop.Read(node.Target); }
                    catch { continue; }

                    if (prop.TextSync && value is string s)
                        (node.TextValues ??= new())[wire] = s;

                    TryAttachValue(node, value, JsonPointer.Join(node.Path, wire));
                }
                break;
            }
    }

    /// <summary>Attach a member/item value if it is a trackable node type.</summary>
    private void TryAttachValue(TrackedNode parent, object? value, string path)
    {
        if (!IsTrackable(value)) return;
        var before = _nodes.Count;
        AttachNode(value!, path);
        if (_nodes.Count > before && _nodes.TryGetValue(value!, out var child))
            parent.Children.Add(child);
    }

    /// <summary>Non-leaf objects are potentially trackable (their subtree may notify).</summary>
    private static bool IsTrackable(object? value)
    {
        if (value is null) return false;
        var t = value.GetType();
        return !VmSerializer.IsLeafType(t) && (t.IsClass || t.IsInterface);
    }

    private void OnPropertyChanged(TrackedNode node, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null) return;
        var wire = VmModel.Camel(e.PropertyName);
        if (!node.Model.Properties.TryGetValue(wire, out var prop)) return;

        var path = JsonPointer.Join(node.Path, wire);
        object? newValue;
        try { newValue = prop.Read(node.Target); }
        catch { return; }

        if (prop.TextSync && newValue is string text)
        {
            var last = node.TextValues?.GetValueOrDefault(wire) ?? "";
            (node.TextValues ??= new())[wire] = text;
            _emit(new PatchOp
            {
                Op = PatchOp.TextPatch,
                Path = path,
                Patch = TextDiffer.CreatePatch(last, text),
                Value = text, // fallback for clients without dmp
            }, prop.SyncReduced);
        }
        else
        {
            _emit(PatchOp.ReplaceOp(path, VmSerializer.ToJson(newValue, path)), prop.SyncReduced);
        }

        // The old subtree under this member is stale — drop watchers, re-walk.
        // [Shared] members emit the replace op but stay untracked here.
        if (!prop.Shared)
            RetrackChild(node, path, newValue);
    }

    private void OnCollectionChanged(TrackedNode node, NotifyCollectionChangedEventArgs args)
    {
        var basePath = node.Path;
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (var i = 0; i < args.NewItems!.Count; i++)
                    _emit(new PatchOp
                    {
                        Op = PatchOp.Add,
                        Path = JsonPointer.Join(basePath, (args.NewStartingIndex + i).ToString()),
                        Value = VmSerializer.ToJson(args.NewItems[i],
                            JsonPointer.Join(basePath, (args.NewStartingIndex + i).ToString())),
                    }, false);
                break;
            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < args.OldItems!.Count; i++)
                    _emit(new PatchOp
                    {
                        Op = PatchOp.Remove,
                        Path = JsonPointer.Join(basePath, args.OldStartingIndex.ToString()),
                    }, false);
                break;
            case NotifyCollectionChangedAction.Replace:
                for (var i = 0; i < args.NewItems!.Count; i++)
                    _emit(new PatchOp
                    {
                        Op = PatchOp.Replace,
                        Path = JsonPointer.Join(basePath, (args.NewStartingIndex + i).ToString()),
                        Value = VmSerializer.ToJson(args.NewItems[i],
                            JsonPointer.Join(basePath, (args.NewStartingIndex + i).ToString())),
                    }, false);
                break;
            case NotifyCollectionChangedAction.Move:
                _emit(new PatchOp
                {
                    Op = PatchOp.Move,
                    From = JsonPointer.Join(basePath, args.OldStartingIndex.ToString()),
                    Path = JsonPointer.Join(basePath, args.NewStartingIndex.ToString()),
                }, false);
                break;
            default: // Reset
                _emit(PatchOp.ReplaceOp(basePath, VmSerializer.ToJson(node.Target, basePath)), false);
                break;
        }

        // Item indices shift on structural change — re-walk all items.
        ResyncItems(node);
    }

    /// <summary>Re-walk collection items after structural change (paths moved).</summary>
    private void ResyncItems(TrackedNode node)
    {
        foreach (var child in node.Children.ToList())
            DetachSubtree(child);
        node.Children.Clear();
        WalkChildren(node);
    }

    /// <summary>Detach the tracked child at <paramref name="path"/>, then attach the new value.</summary>
    private void RetrackChild(TrackedNode parent, string path, object? newValue)
    {
        foreach (var child in parent.Children.ToList())
        {
            if (child.Path == path)
            {
                DetachSubtree(child);
                parent.Children.Remove(child);
            }
        }
        TryAttachValue(parent, newValue, path);
    }

    private void DetachSubtree(TrackedNode node)
    {
        Unsubscribe(node);
        foreach (var child in node.Children.ToList())
            DetachSubtree(child);
        node.Children.Clear();
        _nodes.Remove(node.Target);
    }

    private static void Unsubscribe(TrackedNode node)
    {
        if (node.PropHandler is not null && node.Target is INotifyPropertyChanged inpc)
            inpc.PropertyChanged -= node.PropHandler;
        if (node.CollHandler is not null && node.Target is INotifyCollectionChanged incc)
            incc.CollectionChanged -= node.CollHandler;
        node.PropHandler = null;
        node.CollHandler = null;
    }
}
