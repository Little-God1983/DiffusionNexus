using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.CivitaiRecipe;

namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>
/// One row of the recipe view's "Resources used" list (#628): a resource Civitai lists for the
/// image, or a LoRA the image file names that Civitai does not link. A downloadable row wraps an
/// ordinary browser card and its version pick, so the queue, the pre-download dialog and the
/// waitlist treat it exactly like a card.
/// </summary>
public partial class CivitaiRecipeResourceRowViewModel : ObservableObject
{
    internal const string UnlinkedNote = "not linked on Civitai";

    internal static readonly HashSet<CivitaiModelType> LoraTypes =
        [CivitaiModelType.LORA, CivitaiModelType.LoCon, CivitaiModelType.DoRA];

    /// <summary>A resource Civitai lists; <paramref name="card"/> is null when its model did not load.</summary>
    internal CivitaiRecipeResourceRowViewModel(CivitaiRecipeResource resource, CivitaiResultViewModel? card)
    {
        Card = card;
        Pick = card?.Versions.FirstOrDefault(v => v.Version.Id == resource.VersionId);
        var model = resource.Model;
        var version = resource.Version;

        Name = model?.Name ?? version?.Model?.Name ?? $"Version {resource.VersionId}";
        var isLora = model is not null && LoraTypes.Contains(model.Type);
        var typeLabel = model is null ? null : TypeLabel(model.Type);
        VersionLabel = string.Join(" · ", new[] { version?.Name, typeLabel }.Where(s => !string.IsNullOrEmpty(s)));
        TypeBadge = model is null || isLora ? null : typeLabel!.ToUpperInvariant();
        BaseModel = version?.BaseModel ?? string.Empty;
        SizeDisplay = Pick?.SizeDisplay ?? string.Empty;
        WeightDisplay = FormatWeight(resource.Use, isLora);
        CanTick = isLora && Pick is not null;
        ShowEarlyAccessBadge = Pick?.ShowEarlyAccessBadge ?? false;
        IsPermanentlyPaid = Pick?.IsPermanentlyPaid ?? false;
        Note = resource.LoadError
               ?? (card is not null && Pick is null ? "version not listed on the model page" : null);

        var modelId = model?.Id ?? version?.ModelId;
        if (modelId is > 0)
        {
            var host = card?.IsNsfw == true ? "civitai.red" : "civitai.com";
            CivitaiUrl = $"https://{host}/models/{modelId}?modelVersionId={resource.VersionId}";
        }
    }

    /// <summary>A LoRA the image file names but Civitai does not link (private or deleted).</summary>
    internal CivitaiRecipeResourceRowViewModel(RecipeLoraUse unlinked)
    {
        Name = unlinked.Name;
        VersionLabel = unlinked.FileName ?? string.Empty;
        BaseModel = string.Empty;
        SizeDisplay = string.Empty;
        WeightDisplay = FormatWeight(unlinked, isLora: true);
        Note = UnlinkedNote;
    }

    public string Name { get; }

    /// <summary>"v1.0 · LoRA".</summary>
    public string VersionLabel { get; }

    public string BaseModel { get; }
    public string SizeDisplay { get; }

    /// <summary>"3", "0.65"; "weight unknown" for a LoRA the file gives no weight for; "—" for a non-LoRA.</summary>
    public string WeightDisplay { get; }

    /// <summary>Upper-case type for a resource that is not a LoRA ("CHECKPOINT"); null for a LoRA.</summary>
    public string? TypeBadge { get; }

    /// <summary>Why the row cannot be downloaded, when that is not obvious from its type.</summary>
    public string? Note { get; }

    /// <summary>
    /// LoRA / LoCon / DoRA rows that loaded. Checkpoints and other types are shown for information:
    /// the queue writes into the LoRA download destination.
    /// </summary>
    public bool CanTick { get; }

    public bool ShowEarlyAccessBadge { get; }
    public bool IsPermanentlyPaid { get; }

    public CivitaiResultViewModel? Card { get; }
    public CivitaiVersionPickItemViewModel? Pick { get; }

    /// <summary>The model page with this version; null for an unlinked LoRA.</summary>
    public string? CivitaiUrl { get; }

    public bool HasCivitaiUrl => CivitaiUrl is not null;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isInstalled;

    /// <summary>Rows the queue will not take by default (installed, not a LoRA, unavailable) are dimmed, as in the mockup.</summary>
    public double RowOpacity => CanTick && !IsInstalled ? 1.0 : 0.6;

    internal long SizeBytes => Pick?.SizeBytes ?? 0;

    /// <summary>
    /// The card too: the queue's pre-download dialog reads <see cref="CivitaiVersionPickItemViewModel.IsInstalled"/>
    /// to warn before downloading a version that is already in the library.
    /// </summary>
    internal void ApplyInstalledIndex(CivitaiInstalledIndex index)
    {
        Card?.ApplyInstalledIndex(index);
        IsInstalled = Pick is not null && index.IsInstalled(Pick.Version);
    }

    private static string TypeLabel(CivitaiModelType type) => type switch
    {
        CivitaiModelType.LORA => "LoRA",
        CivitaiModelType.TextualInversion => "Embedding",
        _ => type.ToString(),
    };

    /// <summary>A LoRA the graph loads twice with different weights shows both ("1 / 0.8").</summary>
    private static string FormatWeight(RecipeLoraUse? use, bool isLora)
    {
        if (use?.AllWeights is { Count: > 1 } all) return string.Join(" / ", all.Select(w => Number(w) ?? "unknown"));
        return Number(use?.Weight) ?? (isLora ? "weight unknown" : "—");

        static string? Number(double? weight) => weight?.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
