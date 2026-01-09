using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Notes sub-tab within dataset version detail view.
/// Provides a two-panel layout with notes list and text editor.
/// </summary>
public partial class NotesSubTabView : UserControl
{
    public NotesSubTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnNoteItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not NoteViewModel note) return;
        if (DataContext is not NotesTabViewModel viewModel) return;

        viewModel.SelectedNote = note;
    }
}
