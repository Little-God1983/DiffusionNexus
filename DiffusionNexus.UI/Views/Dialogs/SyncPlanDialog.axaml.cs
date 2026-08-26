using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Confirmation dialog for a metadata sync run: shows what the plan would do per step and
/// returns the options to run with. Closing the window without pressing Start is a cancel.
/// </summary>
public partial class SyncPlanDialog : Window
{
    private SyncPlanDialogViewModel? _viewModel;

    public SyncPlanDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>The confirmed options, or null when the user backed out.</summary>
    public SyncPlanDialogResult? Result { get; private set; }

    public SyncPlanDialog WithViewModel(SyncPlanDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        return this;
    }

    private void OnStartClick(object? sender, RoutedEventArgs e)
    {
        Result = _viewModel?.BuildResult();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
