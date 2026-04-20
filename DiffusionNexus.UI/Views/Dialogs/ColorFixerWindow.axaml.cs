using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Dialogs;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Window for resolving color distribution issues by auto-correcting white balance and brightness.
/// </summary>
public partial class ColorFixerWindow : Window
{
    public ColorFixerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Initializes the window with a pre-configured ViewModel and dialog service.
    /// </summary>
    public ColorFixerWindow(ColorFixerViewModel viewModel, IDialogService dialogService)
        : this()
    {
        viewModel.DialogService = dialogService;
        DataContext = viewModel;
    }
}
