using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for resolving and downloading a LoRA from a Civitai URL.
/// </summary>
public partial class DownloadLoraDialog : Window
{
    private DownloadLoraDialogViewModel? _viewModel;

    public DownloadLoraDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public DownloadLoraResult? Result { get; private set; }

    public DownloadLoraDialog WithViewModel(DownloadLoraDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        return this;
    }

    private void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.ResolvedVersion is null)
        {
            Result = DownloadLoraResult.Cancelled();
            Close(false);
            return;
        }

        var primaryFile = _viewModel.ResolvedVersion.Files.FirstOrDefault(f => f.Primary == true)
                          ?? _viewModel.ResolvedVersion.Files.FirstOrDefault();

        Result = new DownloadLoraResult
        {
            Confirmed = true,
            ModelName = _viewModel.ResolvedModel?.Name ?? _viewModel.PreviewName,
            Version = _viewModel.ResolvedVersion,
            DownloadUrl = primaryFile?.DownloadUrl ?? _viewModel.ResolvedVersion.DownloadUrl,
            FileName = primaryFile?.Name,
            TargetFolder = _viewModel.GetTargetFolder()
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = DownloadLoraResult.Cancelled();
        Close(false);
    }
}

/// <summary>
/// Result returned from the Download LoRA dialog.
/// </summary>
public sealed record DownloadLoraResult
{
    public bool Confirmed { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public CivitaiModelVersion? Version { get; init; }
    public string? DownloadUrl { get; init; }
    public string? FileName { get; init; }
    public string? TargetFolder { get; init; }

    public static DownloadLoraResult Cancelled() => new() { Confirmed = false };
}
