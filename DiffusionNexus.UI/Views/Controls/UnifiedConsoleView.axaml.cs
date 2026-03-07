using System.Collections.Specialized;
using Avalonia.Controls;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Code-behind for the Unified Console view.
/// Replaces both the Activity Log panel and the Installer Manager's Process Console.
/// </summary>
public partial class UnifiedConsoleView : UserControl
{
    public UnifiedConsoleView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is UnifiedConsoleViewModel vm)
        {
            vm.FilteredEntries.CollectionChanged += OnFilteredEntriesChanged;
        }
    }

    private void OnFilteredEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add)
        {
            var listBox = this.FindControl<ListBox>("LogEntriesListBox");
            if (listBox?.ItemCount > 0)
            {
                listBox.ScrollIntoView(listBox.ItemCount - 1);
            }
        }
    }
}
