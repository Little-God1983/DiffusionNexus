using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// The LoRA Sorter's list of files a sort will skip as duplicates, each with the identical copy it
/// matched (#651). Read-only — Close is the only way out.
/// </summary>
public partial class SortDuplicatesDialog : Window
{
    public SortDuplicatesDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SortDuplicatesDialog WithViewModel(SortDuplicatesDialogViewModel viewModel)
    {
        DataContext = viewModel;
        return this;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
