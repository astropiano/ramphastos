using System.Collections.ObjectModel;
using Astropiano.Ramphastos.Core;

public class CounterVm : ApplicationModel
{
    private int _count;
    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    private string _title = "Ramphastos vNext";
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public ObservableCollection<string> Items { get; } = new();

    [Command]
    public Task Increment(int by = 1)
    {
        Count += by;
        return Task.CompletedTask;
    }

    [Command]
    public Task Decrement()
    {
        Count--;
        return Task.CompletedTask;
    }

    [Command]
    public Task<string> Echo(string text) => Task.FromResult(text);

    [Command]
    public Task AddItem(string text)
    {
        Items.Add(text);
        return Task.CompletedTask;
    }

    [Command]
    public Task ClearItems()
    {
        Items.Clear();
        return Task.CompletedTask;
    }

    [ServerOnly]
    public string InternalState { get; set; } = "not-for-clients";
}
