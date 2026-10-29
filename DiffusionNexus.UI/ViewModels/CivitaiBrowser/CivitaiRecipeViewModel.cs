using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.CivitaiRecipe;

namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>
/// The recipe view the Browse Civitai tab shows for a pasted image link (#628): the image with
/// Civitai's SFW/NSFW rating, its generation data, and the resources it was made with — the LoRAs
/// that are not installed ticked, ready for one "Add N to queue". A pasted link ignores every
/// browser filter, Show NSFW included (owner, 2026-10-28): nothing in the recipe is hidden.
/// </summary>
public partial class CivitaiRecipeViewModel : ObservableObject
{
    internal const string NoDataText =
        "This image's file carries no generation data, and Civitai does not publish it either. " +
        "The prompt, settings and LoRA weights are unknown.";

    private const string ResourcesStillListedText =
        "The list of resources below still comes from Civitai, so the LoRAs can be downloaded.";

    /// <summary>
    /// The public API's <c>modelVersionIds</c> can be empty while the web page lists resources (image
    /// 146878246, 2026-10-28): the page reads them from an endpoint that needs the user's API key.
    /// </summary>
    internal const string NoResourcesWithoutKeyCopy =
        "Civitai's public API lists no resources for this image. With your Civitai API key (Settings) the app " +
        "can read the list the image page shows. Until then, open the image on Civitai to see it.";

    internal const string NoResourcesCopy = "Civitai lists no resources for this image.";

    private const string Unknown = "unknown";
    private const string ZeroedNegativeText = "none (the workflow zeroes out the negative prompt)";

    private readonly IClipboardService? _clipboard;

    /// <summary>The ticks the last default pass set; while the user has not changed them, a newer installed snapshot redoes them.</summary>
    private HashSet<CivitaiRecipeResourceRowViewModel> _defaultTicks = [];

    public CivitaiRecipeViewModel(
        CivitaiImageRecipe recipe,
        CivitaiInstalledIndex installed,
        Func<CivitaiModel, CivitaiResultViewModel> cardFactory,
        IClipboardService? clipboard = null)
    {
        Recipe = recipe;
        _clipboard = clipboard;

        foreach (var resource in recipe.Resources)
        {
            Rows.Add(new CivitaiRecipeResourceRowViewModel(resource, resource.Model is null ? null : cardFactory(resource.Model)));
        }
        foreach (var unlinked in recipe.UnlinkedLoras)
        {
            Rows.Add(new CivitaiRecipeResourceRowViewModel(unlinked));
        }

        var image = recipe.Image;
        Title = $"Image {recipe.ImageId} by {image.Username ?? Unknown}";
        var size = image.Width is int w && image.Height is int h ? $"{w} × {h}" : null;
        Details = string.Join(" · ", new[] { size, image.BaseModel }.Where(s => !string.IsNullOrEmpty(s)));
        IsSafe = recipe.IsSafe;
        RatingBadge = $"{(IsSafe ? "SFW" : "NSFW")} · {recipe.RatingLabel}";
        IsVideo = recipe.Data.Format == RecipeFileFormat.Video;

        var data = recipe.Data;
        HasGenerationData = data.Source != RecipeDataSource.None;
        NoDataWarning = string.Join(" ", new[]
        {
            NoDataText,
            data.SavedBy is { } program ? $"The file was last saved by {program}, which may have removed it." : null,
            Rows.Count > 0 ? ResourcesStillListedText : null,
        }.Where(part => part is not null));
        SourceNote = data.SourceNote;
        ReadError = data.ReadError;
        // A parameters text without a prompt line has none; a graph whose prompt could not be traced
        // has one nobody could read.
        var missing = data.Source == RecipeDataSource.SettingsText ? "none given" : Unknown;
        PromptText = data.Prompt ?? missing;
        NegativePromptText = data.NegativeZeroedOut ? ZeroedNegativeText : data.NegativePrompt ?? missing;
        CheckpointText = data.Checkpoint ?? Unknown;
        VaeText = data.Vae ?? Unknown;
        SamplerLine = (data.Sampler, data.Scheduler) switch
        {
            (null, null) => Unknown,
            (var s, null) => s!,
            (null, var sch) => sch!,
            var (s, sch) => $"{s} · {sch}",
        };
        StepsCfgSeedLine = string.Join(" · ",
            data.Steps?.ToString(CultureInfo.InvariantCulture) ?? Unknown,
            data.Cfg?.ToString("0.###", CultureInfo.InvariantCulture) ?? Unknown,
            data.Seed?.ToString(CultureInfo.InvariantCulture) ?? Unknown);
        FluxGuidanceText = data.Guidance?.ToString("0.###", CultureInfo.InvariantCulture);

        foreach (var row in Rows)
        {
            row.ApplyInstalledIndex(installed);
            row.PropertyChanged += OnRowPropertyChanged;
        }
        ApplyDefaultTicks();
    }

    public CivitaiImageRecipe Recipe { get; }

    public ObservableCollection<CivitaiRecipeResourceRowViewModel> Rows { get; } = [];

    public string Title { get; }
    public string Details { get; }

    /// <summary>"SFW · PG", "NSFW · X", "NSFW · unrated" — Civitai's rating, judged by the app's NSFW rule.</summary>
    public string RatingBadge { get; }

    public bool IsSafe { get; }
    public bool IsVideo { get; }

    [ObservableProperty]
    private Bitmap? _previewImage;

    public bool HasGenerationData { get; }
    public string? SourceNote { get; }
    /// <summary>
    /// What the file lacks, the program that last saved it when the file says, and — only when there is
    /// one — that the resource list below still works.
    /// </summary>
    public string NoDataWarning { get; }

    /// <summary>False when Civitai's public API listed no resources and the file named no LoRA.</summary>
    public bool HasResources => Rows.Count > 0;

    public string NoResourcesText => Recipe.HasApiKey ? NoResourcesCopy : NoResourcesWithoutKeyCopy;

    /// <summary>A part of the file that was there but could not be read (shown under the warning).</summary>
    public string? ReadError { get; }

    public string PromptText { get; }
    public string NegativePromptText { get; }
    public string CheckpointText { get; }
    public string VaeText { get; }

    /// <summary>"exponential/ddim_beta57", "euler · karras" or "unknown".</summary>
    public string SamplerLine { get; }

    /// <summary>"8 · 1 · 832350830334358", each missing part "unknown".</summary>
    public string StepsCfgSeedLine { get; }

    /// <summary>Flux's built-in guidance (#674); null when the file has none, and then the row is hidden.</summary>
    public string? FluxGuidanceText { get; }

    [ObservableProperty]
    private bool _isPromptExpanded;

    public string PromptToggleText => IsPromptExpanded ? "Show less" : "Show all";

    public int SelectedCount => SelectedRows().Count();

    public string FooterText =>
        $"{SelectedCount} selected · {CivitaiVersionPickItemViewModel.FormatSize(SelectedRows().Sum(r => r.SizeBytes))}";

    public string AddToQueueText => $"Add {SelectedCount} to queue";

    public bool HasSelection => SelectedCount > 0;

    /// <summary>Send to ComfyUI is #665; its button is shown disabled so its place is visible.</summary>
    public bool CanSendToComfyUi => false;

    public string SendToComfyUiTip => "Coming in a later version";

    /// <summary>
    /// False while the host has not read the library yet (#667): every row would look not installed, so
    /// "Add to queue" waits instead of queueing installed LoRAs.
    /// </summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    private bool _isLibraryRead = true;

    public Action<List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)>>? EnqueueHandler { get; set; }

    public Action<string>? UrlOpener { get; set; }

    public List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> SelectedPairs() =>
        SelectedRows().Select(r => (r.Card!, r.Pick!)).ToList();

    /// <summary>Applies a newer installed snapshot; redoes the default ticks unless the user changed them.</summary>
    public void ApplyInstalledIndex(CivitaiInstalledIndex index)
    {
        var untouched = Rows.Where(r => r.IsSelected).ToHashSet().SetEquals(_defaultTicks);
        foreach (var row in Rows) row.ApplyInstalledIndex(index);
        if (untouched) ApplyDefaultTicks();
    }

    /// <summary>Decodes the cached original as the preview. Nothing to do for a video or a failed download.</summary>
    public async Task LoadPreviewAsync(CancellationToken ct)
    {
        if (Recipe.OriginalFilePath is not { } path || Recipe.Data.Format is not (RecipeFileFormat.Png or RecipeFileFormat.Jpeg or RecipeFileFormat.WebP))
        {
            return;
        }

        var bitmap = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 600, BitmapInterpolationMode.HighQuality);
        }, ct);
        if (ct.IsCancellationRequested)
        {
            bitmap.Dispose();
            return;
        }
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => PreviewImage = bitmap);
    }

    [RelayCommand]
    private void TogglePrompt() => IsPromptExpanded = !IsPromptExpanded;

    partial void OnIsPromptExpandedChanged(bool value) => OnPropertyChanged(nameof(PromptToggleText));

    [RelayCommand]
    private async Task CopyPromptAsync()
    {
        if (_clipboard is not null && Recipe.Data.Prompt is { } prompt) await _clipboard.SetTextAsync(prompt);
    }

    [RelayCommand]
    private void OpenOnCivitai() => UrlOpener?.Invoke(Recipe.CivitaiUrl);

    [RelayCommand]
    private void OpenResource(CivitaiRecipeResourceRowViewModel? row)
    {
        if (row?.CivitaiUrl is { } url) UrlOpener?.Invoke(url);
    }

    [RelayCommand(CanExecute = nameof(IsLibraryRead))]
    private void AddToQueue()
    {
        var pairs = SelectedPairs();
        if (pairs.Count > 0) EnqueueHandler?.Invoke(pairs);
    }

    private IEnumerable<CivitaiRecipeResourceRowViewModel> SelectedRows() => Rows.Where(r => r.IsSelected && r.CanTick);

    private void ApplyDefaultTicks()
    {
        foreach (var row in Rows) row.IsSelected = row.CanTick && !row.IsInstalled;
        _defaultTicks = Rows.Where(r => r.IsSelected).ToHashSet();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CivitaiRecipeResourceRowViewModel.IsSelected)) return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(AddToQueueText));
        OnPropertyChanged(nameof(HasSelection));
    }
}
