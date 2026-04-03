using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for choosing the download destination of a LoRA version.
/// </summary>
public partial class DownloadLoraVersionDialog : Window
{
    private DownloadLoraVersionDialogViewModel? _viewModel;

    public DownloadLoraVersionDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the result after the dialog closes.
    /// Null if cancelled.
    /// </summary>
    public DownloadLoraVersionResult? Result { get; private set; }

    /// <summary>
    /// Initializes the dialog with version and source folder information.
    /// </summary>
    public DownloadLoraVersionDialog WithVersionInfo(
        string modelName,
        CivitaiModelVersion civitaiVersion,
        IReadOnlyList<string> sourceFolders,
        string? category = null)
    {
        _viewModel = new DownloadLoraVersionDialogViewModel();
        _viewModel.Initialize(modelName, civitaiVersion, sourceFolders, category);
        DataContext = _viewModel;
        return this;
    }

    private void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Result = DownloadLoraVersionResult.Cancelled();
            Close(false);
            return;
        }

        Result = new DownloadLoraVersionResult
        {
            Confirmed = true,
            TargetFolder = _viewModel.GetTargetFolder()
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = DownloadLoraVersionResult.Cancelled();
        Close(false);
    }
}
