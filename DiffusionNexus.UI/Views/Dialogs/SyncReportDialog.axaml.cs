using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Read-only post-run sync report: per-step counts and grouped, expandable failures.
/// No result — Close is the only way out.
/// </summary>
public partial class SyncReportDialog : Window
{
    public SyncReportDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SyncReportDialog WithViewModel(SyncReportDialogViewModel viewModel)
    {
        DataContext = viewModel;
        return this;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
