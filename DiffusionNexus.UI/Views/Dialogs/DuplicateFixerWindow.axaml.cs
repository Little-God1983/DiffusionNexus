using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Dialogs;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Window for resolving duplicate image issues by comparing and choosing which to keep.
/// </summary>
public partial class DuplicateFixerWindow : Window
{
    public DuplicateFixerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Initializes the window with a pre-configured ViewModel and dialog service.
    /// </summary>
    public DuplicateFixerWindow(DuplicateFixerViewModel viewModel, IDialogService dialogService)
        : this()
    {
        viewModel.DialogService = dialogService;
        DataContext = viewModel;
    }
}
