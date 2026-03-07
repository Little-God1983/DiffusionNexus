using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the image generation metadata side panel in the image viewer.
/// Displays ComfyUI workflow data parsed from PNG text chunks.
/// </summary>
public partial class ImageMetadataPanelViewModel : ObservableObject
{
    private readonly Services.ImageMetadataParser _parser = new();
    private Func<string, Task>? _copyToClipboard;

    [ObservableProperty]
    private bool _isPanelExpanded = true;

    [ObservableProperty]
    private ImageGenerationData? _metadata;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string _statusMessage = "No image loaded";

    [ObservableProperty]
    private bool _positiveCopied;

    [ObservableProperty]
    private bool _negativeCopied;

    /// <summary>Whether any LoRAs were found in the metadata.</summary>
    public bool HasLoras => Metadata?.Loras.Count > 0;

    /// <summary>Whether the denoise value should be shown (only when &lt; 1.0).</summary>
    public bool ShowDenoise => Metadata?.Denoise is not null and < 1.0;

    /// <summary>
    /// Sets the clipboard copy delegate. Should be called from the View code-behind
    /// once the visual tree is attached.
    /// </summary>
    public void SetClipboardAction(Func<string, Task> copyToClipboard)
    {
        _copyToClipboard = copyToClipboard;
    }

    /// <summary>
    /// Parses and loads metadata from the specified image file.
    /// </summary>
    public void LoadMetadata(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            Metadata = null;
            HasData = false;
            StatusMessage = "No image loaded";
            OnDerivedPropertiesChanged();
            return;
        }

        try
        {
            var ext = Path.GetExtension(imagePath);
            if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                Metadata = new ImageGenerationData
                {
                    FileName = Path.GetFileName(imagePath),
                    HasData = false
                };
                HasData = false;
                StatusMessage = "No generation data found in this image";
                OnDerivedPropertiesChanged();
                return;
            }

            Metadata = _parser.Parse(imagePath);
            HasData = Metadata.HasData;
            StatusMessage = HasData ? "" : "No generation data found in this image";
        }
        catch (Exception ex)
        {
            HasData = false;
            StatusMessage = $"Error reading metadata: {ex.Message}";
        }

        OnDerivedPropertiesChanged();
    }

    [RelayCommand]
    private void TogglePanel()
    {
        IsPanelExpanded = !IsPanelExpanded;
    }

    [RelayCommand]
    private async Task CopyPositivePromptAsync()
    {
        if (Metadata?.PositivePrompt is null || _copyToClipboard is null) return;

        await _copyToClipboard(Metadata.PositivePrompt);
        PositiveCopied = true;
        await Task.Delay(1500);
        PositiveCopied = false;
    }

    [RelayCommand]
    private async Task CopyNegativePromptAsync()
    {
        if (Metadata?.NegativePrompt is null || _copyToClipboard is null) return;

        await _copyToClipboard(Metadata.NegativePrompt);
        NegativeCopied = true;
        await Task.Delay(1500);
        NegativeCopied = false;
    }

    private void OnDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasLoras));
        OnPropertyChanged(nameof(ShowDenoise));
    }
}
