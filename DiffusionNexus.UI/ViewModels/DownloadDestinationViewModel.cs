using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Reusable download-destination picker. Shared between the LoRA Version Downloader
/// dialog and the Civitai Browser queue panel — both want the same UX:
/// "existing source folder" vs. "any folder", with optional base-model and category
/// subfolders. Compute final target paths via <see cref="BuildTargetDirectory"/>.
/// </summary>
public partial class DownloadDestinationViewModel : ObservableObject
{
    private readonly IDialogService? _dialogService;

    public DownloadDestinationViewModel() : this(null) { }

    public DownloadDestinationViewModel(IDialogService? dialogService)
    {
        _dialogService = dialogService;
    }

    public ObservableCollection<string> SourceFolders { get; } = [];

    [ObservableProperty]
    private bool _isDownloadToExisting = true;

    [ObservableProperty]
    private bool _isDownloadToFolder;

    [ObservableProperty]
    private string? _selectedSourceFolder;

    [ObservableProperty]
    private string? _customFolderPath;

    [ObservableProperty]
    private bool _createBaseModelFolder = true;

    [ObservableProperty]
    private bool _createCategoryFolder = true;

    /// <summary>
    /// A representative base model / category pair used purely to render the preview
    /// path. Callers update these when context changes (e.g. the currently selected
    /// browser result).
    /// </summary>
    [ObservableProperty]
    private string? _previewBaseModel;

    [ObservableProperty]
    private string? _previewCategory;

    /// <summary>
    /// When false, the preview-path block is suppressed in the view. The Civitai
    /// browser's queue panel sets this off because each queued item can land in a
    /// different folder (different base model / category); the per-job resolved
    /// path is shown on each queue tile instead.
    /// </summary>
    [ObservableProperty]
    private bool _showPreviewPath = true;

    public bool HasDestinationPreview
        => ShowPreviewPath && IsDownloadToExisting && !string.IsNullOrWhiteSpace(SelectedSourceFolder);

    public string PreviewPath
    {
        get
        {
            if (!IsDownloadToExisting || string.IsNullOrWhiteSpace(SelectedSourceFolder))
                return string.Empty;
            return BuildTargetDirectory(PreviewBaseModel, PreviewCategory) ?? string.Empty;
        }
    }

    public bool HasNoSourceFolders => SourceFolders.Count == 0;

    /// <summary>True when the user has selected a destination usable for downloads.</summary>
    public bool HasUsableDestination
        => (IsDownloadToExisting && !string.IsNullOrWhiteSpace(SelectedSourceFolder))
           || (IsDownloadToFolder && !string.IsNullOrWhiteSpace(CustomFolderPath));

    public async Task InitializeAsync(IReadOnlyList<string> sourceFolders)
    {
        SourceFolders.Clear();
        foreach (var f in sourceFolders) SourceFolders.Add(f);
        SelectedSourceFolder = SourceFolders.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoSourceFolders));
        OnDownloadStateChanged();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the directory a download should land in, given the per-job
    /// <paramref name="baseModel"/> and <paramref name="category"/> context.
    /// Returns null when no destination is configured.
    /// </summary>
    public string? BuildTargetDirectory(string? baseModel, string? category)
    {
        if (IsDownloadToFolder)
        {
            return string.IsNullOrWhiteSpace(CustomFolderPath) ? null : CustomFolderPath;
        }

        if (string.IsNullOrWhiteSpace(SelectedSourceFolder)) return null;

        var path = SelectedSourceFolder;
        if (CreateBaseModelFolder && !string.IsNullOrWhiteSpace(baseModel))
            path = Path.Combine(path, baseModel);
        if (CreateCategoryFolder && !string.IsNullOrWhiteSpace(category))
            path = Path.Combine(path, category);
        return path;
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var dialog = _dialogService ?? App.Services?.GetService<IDialogService>();
        if (dialog is null) return;
        var path = await dialog.ShowOpenFolderDialogAsync("Select Download Folder");
        if (!string.IsNullOrEmpty(path)) CustomFolderPath = path;
    }

    partial void OnIsDownloadToExistingChanged(bool value)
    {
        if (value) IsDownloadToFolder = false;
        OnDownloadStateChanged();
    }

    partial void OnIsDownloadToFolderChanged(bool value)
    {
        if (value) IsDownloadToExisting = false;
        OnDownloadStateChanged();
    }

    partial void OnSelectedSourceFolderChanged(string? value) => OnDownloadStateChanged();
    partial void OnCustomFolderPathChanged(string? value) => OnDownloadStateChanged();
    partial void OnCreateBaseModelFolderChanged(bool value) => OnDownloadStateChanged();
    partial void OnCreateCategoryFolderChanged(bool value) => OnDownloadStateChanged();
    partial void OnPreviewBaseModelChanged(string? value) => OnPropertyChanged(nameof(PreviewPath));
    partial void OnPreviewCategoryChanged(string? value) => OnPropertyChanged(nameof(PreviewPath));
    partial void OnShowPreviewPathChanged(bool value) => OnPropertyChanged(nameof(HasDestinationPreview));

    private void OnDownloadStateChanged()
    {
        OnPropertyChanged(nameof(PreviewPath));
        OnPropertyChanged(nameof(HasDestinationPreview));
        OnPropertyChanged(nameof(HasUsableDestination));
    }
}
