using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Astropiano.Ramphastos.Core;

/// <summary>
/// The unit of synchronization — any object that lives inside a synced graph.
/// Change notification flows through <see cref="INotifyPropertyChanged"/>;
/// collections participate via
/// <see cref="System.Collections.Specialized.INotifyCollectionChanged"/> on
/// the collection itself (e.g. ObservableCollection). The owning session's
/// graph tracker turns notifications into patch ops inside its actor turn —
/// at any depth, and commands ([Command] methods) are invocable on any node.
/// </summary>
public abstract class SyncedObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
