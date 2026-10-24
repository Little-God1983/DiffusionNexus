using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Read-only report the LoRA Sorter shows when a run ends (#651): totals, then the failed,
/// skipped-duplicate, renamed and transferred files by name. Close is the only result.
/// </summary>
public partial class SortReportDialog : Window
{
    public SortReportDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SortReportDialog WithViewModel(SortReportDialogViewModel viewModel)
    {
        DataContext = viewModel;
        return this;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
