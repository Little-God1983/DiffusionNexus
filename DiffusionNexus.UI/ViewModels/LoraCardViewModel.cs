using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraCardViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private ModelClass? _model;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private string? folderPath;

    [ObservableProperty]
    private string? treePath;

    public ObservableCollection<ModelVariantViewModel> Variants { get; } = new();

    [ObservableProperty]
    private ModelVariantViewModel? selectedVariant;

    public IEnumerable<string> DiffusionTypes => Model is null
        ? Array.Empty<string>()
        : new[] { Model.ModelType.ToString() };

    public string DiffusionBaseModel => Model?.DiffusionBaseModel ?? string.Empty;

    public IRelayCommand EditCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand OpenWebCommand { get; }
    public IAsyncRelayCommand CopyCommand { get; }
    public IAsyncRelayCommand CopyNameCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }

    public LoraHelperViewModel? Parent { get; set; }

    public LoraCardViewModel()
    {
        EditCommand = new RelayCommand(OnEdit);
        DeleteCommand = new AsyncRelayCommand(OnDeleteAsync);
        OpenWebCommand = new AsyncRelayCommand(OnOpenWebAsync);
        CopyCommand = new AsyncRelayCommand(OnCopyAsync);
        CopyNameCommand = new AsyncRelayCommand(OnCopyNameAsync);
        OpenFolderCommand = new RelayCommand(OnOpenFolder);
        Variants.CollectionChanged += OnVariantsCollectionChanged;
    }

    private void OnVariantsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasMultipleVariants));
    }

    partial void OnSelectedVariantChanged(ModelVariantViewModel? value)
    {
        Model = value?.Model;
    }

    partial void OnModelChanged(ModelClass? value)
    {
        _ = LoadPreviewImageAsync();
    }

    public void InitializeVariants(IEnumerable<ModelVariantViewModel> variants)
    {
        Variants.Clear();

        var ordered = variants
            .OrderByDescending(v => v.IsDefaultVariant)
            .ThenBy(v => v.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Model.SafeTensorFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var variant in ordered)
        {
            Variants.Add(variant);
        }

        SelectedVariant = Variants.FirstOrDefault();
    }

    public bool RemoveVariant(ModelVariantViewModel variant)
    {
        var removed = Variants.Remove(variant);
        if (!removed)
        {
            return false;
        }

        if (Variants.Count == 0)
        {
            SelectedVariant = null;
        }
        else if (ReferenceEquals(SelectedVariant, variant))
        {
            SelectedVariant = Variants.FirstOrDefault();
        }

        return true;
    }

    public bool MatchesSearch(string search)
    {
        return Variants.Any(variant => variant.MatchesSearch(search));
    }

    public string GetSearchIndexText()
    {
        return string.Join(" ", Variants.Select(v => v.SearchText));
    }

    public IEnumerable<string> GetAllDiffusionBaseModels()
    {
        return Variants
            .Select(v => v.Model.DiffusionBaseModel)
            .Where(name => !string.IsNullOrWhiteSpace(name));
    }

    public bool HasAnySafeVariant => Variants.Any(v => v.Model.Nsfw != true);

    public bool HasMultipleVariants => Variants.Count > 1;

    public bool MatchesBaseModel(HashSet<string> baseModels)
    {
        return Variants.Any(v => baseModels.Contains(v.Model.DiffusionBaseModel));
    }

    public string SortKey => Variants
        .Select(v => v.Model.SafeTensorFileName)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault() ?? string.Empty;

    public DateTime NewestCreationDate => Variants
        .Select(v => v.Model?.AssociatedFilesInfo
            .FirstOrDefault(f =>
                f.Extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase) ||
                f.Extension.Equals(".pt", StringComparison.OrdinalIgnoreCase))?.CreationTime ?? DateTime.MinValue)
        .DefaultIfEmpty(DateTime.MinValue)
        .Max();

    private async Task LoadPreviewImageAsync()
    {
        var path = GetPreviewImagePath();
        if (path is null || !File.Exists(path))
        {
            var media = GetPreviewMediaPath();
            if (media is not null && ThumbnailSettings.GenerateVideoThumbnails)
            {
                path = await ThumbnailGenerator.GenerateThumbnailAsync(media);
            }
        }

        if (path is null || !File.Exists(path))
        {
            PreviewImage = null;
            return;
        }

        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            });
            await Dispatcher.UIThread.InvokeAsync(() => PreviewImage = bitmap);
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => PreviewImage = null);
        }
    }

    public string? GetPreviewImagePath()
    {
        if (Model == null) return null;

        foreach (var ext in SupportedTypes.ImageTypesByPriority)
        {
            var file = Model.AssociatedFilesInfo.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (file != null)
                return file.FullName;
        }

        return null;
    }

    private string? GetPreviewMediaPath()
    {
        if (Model == null) return null;

        foreach (var ext in SupportedTypes.VideoTypesByPriority)
        {
            var file = Model.AssociatedFilesInfo.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (file != null)
                return file.FullName;
        }

        return null;
    }

    private void OnEdit()
    {
        var name = SelectedVariant?.Model.SafeTensorFileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Log($"Edit {name}", LogSeverity.Info);
    }

    private Task OnDeleteAsync()
    {
        if (SelectedVariant?.Model == null)
        {
            return Task.CompletedTask;
        }

        return Parent?.DeleteCardAsync(this) ?? Task.CompletedTask;
    }

    private async Task OnOpenWebAsync()
    {
        if (Parent == null || SelectedVariant?.Model == null)
            return;

        await Parent.OpenWebForCardAsync(this);
    }

    private async Task OnCopyAsync()
    {
        if (Parent == null || SelectedVariant?.Model == null)
            return;

        await Parent.CopyTrainedWordsAsync(this);
    }

    private async Task OnCopyNameAsync()
    {
        if (Parent == null || SelectedVariant?.Model == null)
            return;

        await Parent.CopyModelNameAsync(this);
    }

    private void OnOpenFolder()
    {
        if (string.IsNullOrWhiteSpace(FolderPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = FolderPath,
                UseShellExecute = true,
                Verb = "open",
            });
        }
        catch (Exception ex)
        {
            Log($"failed to open folder: {ex.Message}", LogSeverity.Error);
        }
    }
}
