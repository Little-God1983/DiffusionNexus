using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Dialogs;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Window that lets the user resolve groups of byte-identical LoRA files by
/// picking which copy to keep. The remaining files (and their sidecars) are
/// deleted from disk and pruned from the database.
/// </summary>
public partial class LoraDuplicateFixerWindow : Window
{
    public LoraDuplicateFixerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public LoraDuplicateFixerWindow(LoraDuplicateFixerViewModel viewModel, IDialogService dialogService)
        : this()
    {
        viewModel.DialogService = dialogService;
        DataContext = viewModel;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
