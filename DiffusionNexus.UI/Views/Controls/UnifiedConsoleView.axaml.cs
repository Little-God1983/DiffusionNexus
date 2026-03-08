using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;
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

            // Register clipboard handler using TopLevel — the ViewModel has no visual
            // reference, and Window.Clipboard is deprecated in Avalonia 11+.
            vm.RegisterClipboardHandler(async text =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                var clipboard = topLevel?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(text);
                }
            });
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
