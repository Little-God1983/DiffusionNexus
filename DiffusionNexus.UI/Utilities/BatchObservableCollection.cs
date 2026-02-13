using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that supports batch operations.
/// <para>
/// <see cref="ReplaceAll"/> swaps the entire backing list in one shot and fires
/// a single <see cref="NotifyCollectionChangedAction.Reset"/> event, avoiding
/// per-item layout passes in virtualizing panels.
/// </para>
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces the entire collection content with <paramref name="items"/> and fires
    /// a single <see cref="NotifyCollectionChangedAction.Reset"/> notification.
    /// </summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
    }
}
