using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.CivitaiRecipe;

namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>
/// One card of the Recipes tab (#667). A recipe folder that could not be read is a card too: it is
/// counted, selectable and deletable, but never Ready or Missing.
/// </summary>
public partial class RecipeCardViewModel : ObservableObject
{
    internal const string BrokenTitle = "Recipe could not be read";
    private const int TitleLength = 60;

    private readonly Action? _selectionChanged;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isMissing;
    [ObservableProperty] private bool _isKept;
    [ObservableProperty] private bool _isSelected;

    public RecipeCardViewModel(StoredRecipe stored, DateTime nowLocal, Action? selectionChanged = null)
    {
        Stored = stored;
        ImageId = stored.ImageId;
        _selectionChanged = selectionChanged;

        var saved = stored.Saved;
        if (saved is null)
        {
            Title = BrokenTitle;
            Byline = stored.ReadError ?? "";
            RatingBadge = "";
            IsSafe = true;
            PastedAt = BrokenPastedAt(stored.Folder);
            StatusText = "";
            SearchText = stored.ImageId.ToString(CultureInfo.InvariantCulture);
            return;
        }

        var recipe = saved.Recipe;
        PastedAt = saved.PastedAt;
        IsKept = saved.Kept;
        IsSafe = recipe.IsSafe;
        RatingBadge = $"{(recipe.IsSafe ? "SFW" : "NSFW")} · {recipe.RatingLabel}";
        Title = TitleOf(recipe);
        Byline = $"{recipe.Image.Username ?? "unknown creator"} · {RecipeDateGrouper.TimeLabel(saved.PastedAt, nowLocal)}"
                 + (recipe.Data.Prompt is null ? " · no prompt" : "");
        BaseModel = BaseModelOf(recipe);
        ThumbnailPath = recipe.OriginalFilePath;
        SearchText = string.Join('\n', new[] { recipe.Data.Prompt, recipe.Image.Username, ImageId.ToString(CultureInfo.InvariantCulture) }
            .Concat(recipe.Resources.Select(r => r.Model?.Name))
            .Concat(recipe.Resources.Select(r => r.Use?.Name))
            .Concat(recipe.UnlinkedLoras.Select(u => u.Name))
            .Where(s => !string.IsNullOrEmpty(s))!);
    }

    public StoredRecipe Stored { get; }
    public long ImageId { get; }
    public string Title { get; }
    public string Byline { get; }
    public string RatingBadge { get; }
    public bool IsSafe { get; }
    public string? BaseModel { get; }
    public string? ThumbnailPath { get; }
    public bool IsBroken => Stored.IsBroken;
    public DateTimeOffset PastedAt { get; }

    /// <summary>What the search box looks through: prompt, creator, id, LoRA and resource names.</summary>
    internal string SearchText { get; }

    /// <summary>The LoRAs the card's recipe is missing, set by <see cref="ApplyStatus"/>.</summary>
    internal RecipeStatus? Status { get; private set; }

    internal const string CheckingText = "Checking library…";

    /// <summary>
    /// Judges the recipe against the library; a broken recipe is neither Ready nor Missing, and neither is
    /// any recipe while the library has not been read (<paramref name="libraryRead"/> false).
    /// </summary>
    internal void ApplyStatus(CivitaiInstalledIndex installed, bool libraryRead = true)
    {
        if (Stored.Saved is null) return;
        if (!libraryRead)
        {
            Status = null;
            StatusText = CheckingText;
            IsReady = false;
            IsMissing = false;
            return;
        }
        Status = RecipeStatus.Compute(Stored.Saved.Recipe, installed);
        StatusText = Status.Text;
        IsReady = Status.IsReady;
        IsMissing = Status.IsMissing;
    }

    /// <summary>Takes over what the user did to the card it replaces after a reload.</summary>
    internal void CopyStateFrom(RecipeCardViewModel previous) => IsSelected = previous.IsSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();

    private static string TitleOf(CivitaiImageRecipe recipe)
    {
        var line = recipe.Data.Prompt?
            .Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 0);
        if (line is null) return $"Image {recipe.ImageId}";
        return line.Length > TitleLength ? line[..TitleLength].TrimEnd() : line;
    }

    private static string? BaseModelOf(CivitaiImageRecipe recipe)
    {
        if (!string.IsNullOrWhiteSpace(recipe.Image.BaseModel)) return recipe.Image.BaseModel;
        var checkpoint = recipe.Resources.FirstOrDefault(r => r.Model?.Type == CivitaiModelType.Checkpoint && r.Version?.BaseModel is { Length: > 0 });
        var any = checkpoint ?? recipe.Resources.FirstOrDefault(r => r.Version?.BaseModel is { Length: > 0 });
        return any?.Version?.BaseModel;
    }

    private static DateTimeOffset BrokenPastedAt(string folder)
    {
        try { return new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder), TimeSpan.Zero); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTimeOffset.Now; }
    }
}
