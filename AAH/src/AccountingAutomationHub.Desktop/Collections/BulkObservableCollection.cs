using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AccountingAutomationHub.Desktop.Collections;

/// <summary>
/// ObservableCollection optimized for replacing large result sets with one UI reset.
/// This avoids thousands of WPF CollectionChanged events when a client has many rows.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        RaiseReset();
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var changed = false;
        foreach (var item in items)
        {
            Items.Add(item);
            changed = true;
        }
        if (changed) RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
