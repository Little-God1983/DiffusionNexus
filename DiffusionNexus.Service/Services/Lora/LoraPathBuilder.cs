using System.Text.RegularExpressions;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync;
namespace DiffusionNexus.Service.Services.Lora;

/// <summary>
/// Pure path construction for the LoRA Sorter: folder naming, sanitization
/// (nothing in the download path sanitizes — this is deliberately new), and the
/// deterministic collision rename convention shared with
/// DownloadCollisionPolicy
/// ({stem}_{versionId}{ext}), so re-runs are idempotent.
/// </summary>
public static partial class LoraPathBuilder
{
    public const string UnknownFolderName = "Unknown";

    /// <summary>
    /// Whether a base model is the "???" placeholder (or nothing at all) rather than an answer —
    /// the test that picks the Unknown bucket.
    /// </summary>
    /// <remarks>
    /// Delegates rather than restating: <see cref="SyncStateDeriver.IsPlaceholder"/> is the same
    /// rule on the WRITE side of the boundary — it decides whether <c>IdentifyModelStep</c> may fill
    /// the very label this then reads to pick a folder. Three verbatim copies of
    /// <c>IsNullOrWhiteSpace(x) || x == "???"</c> existed, one on each side of that boundary, which
    /// is exactly the drift the sorter's own doc comments argue against.
    /// </remarks>
    public static bool IsPlaceholderBaseModel(string? baseModel) => SyncStateDeriver.IsPlaceholder(baseModel);

    /// <summary>
    /// Whether a file is one shard of a model split across several files —
    /// <c>model-00001-of-00004.safetensors</c> — and therefore has no destination of its own.
    /// </summary>
    /// <remarks>
    /// A shard is a fragment of ONE logical model, not a model. The sorter plans file by file and
    /// cannot see a candidate's siblings, so it can route one shard somewhere its siblings do not
    /// go, and a split shard set is worse than an unsorted one: the halves are individually useless
    /// and the loader needs the whole complement plus its index to open anything at all. Refusing
    /// the move is the only answer the planner can give correctly from the information it has.
    /// <para>
    /// The reason holds for EVERY destination, so <see cref="LoraSortPlanner"/> applies it to the
    /// whole routing decision and not to the support-asset arm alone — which folder a subset was
    /// headed for has nothing to do with why splitting the set is wrong. Guarding one arm splits a
    /// mixed-kind set through the other: this became reachable when
    /// <see cref="Sync.Identity.AssetKindHeaderMap"/> learned to read a root-anchored LLM decoder,
    /// and three of the four shards of a LLaVA-OneVision checkpoint now answer TextEncoder while
    /// the fourth is a vision tower and answers LORA — so a kind-folder-only guard would keep three
    /// in place and let the fourth sort away by base model. Every one of those per-file verdicts is
    /// correct; it is the ROUTING that has to know better, which is why the rule lives here and not
    /// in the header map. What the file IS is still recorded.
    /// </para>
    /// <para>
    /// This does not cost ordinary LoRA sorting anything: the convention belongs to large
    /// multi-gigabyte base models, and a LoRA is single-file by nature. A file in a LoRA source that
    /// genuinely carries the pattern IS a fragment of a split model, and leaving it alone is right
    /// whatever base model it claims — the failure direction is "left where it was", which is always
    /// recoverable.
    /// </para>
    /// <para>
    /// The pattern is HuggingFace's <c>save_pretrained</c> convention and nothing else in a model
    /// library is named that way: a five-digit index, "-of-", a five-digit total, immediately before
    /// the extension. Anchored at the end for that reason — a LoRA merely CONTAINING those digits
    /// mid-name keeps its destination.
    /// </para>
    /// </remarks>
    public static bool IsShardOfASplitModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var stem = Path.GetFileNameWithoutExtension(path);
        return ShardSuffix().IsMatch(stem);
    }

    [GeneratedRegex(@"-\d{5}-of-\d{5}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShardSuffix();

    /// <summary>
    /// Folder-name sanitization. Windows-only rules today.
    /// </summary>
    /// <remarks>
    /// TODO: Linux Implementation for LoRA Sorter: the TrimEnd('.', ' ') below is a Win32
    /// restriction, and Path.GetInvalidFileNameChars() returns only NUL and '/' on Linux —
    /// so a base-model or category name carrying \ : * ? " &lt; &gt; | would pass straight into a
    /// created directory name there, and such a folder is then awkward on any Windows client
    /// sharing the library. A Linux build should sanitize against the union of both platforms'
    /// invalid sets; this method is the single seam for that (make it an injectable policy
    /// rather than adding a second static).
    /// </remarks>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).TrimEnd('.', ' ');
        return sanitized.Length == 0 ? "_" : sanitized;
    }

    /// <summary>
    /// An unresolved category contributes NO segment, exactly as
    /// <c>DownloadDestinationViewModel.BuildTargetDirectory</c> omits it for a null/empty
    /// category: the file lands in <c>{root}\{BaseModel}\</c>. The sorter used to append
    /// <c>Unknown\</c> unconditionally, so — both features defaulting their folder toggles
    /// to true — every sort run dragged uncategorized downloads into <c>Unknown\</c> and the
    /// next download re-created them one level up, forever.
    /// A placeholder base model keeps its <c>Unknown\</c> folder: unlike the category, the
    /// base-model segment is the top-level bucket and files with no base model need one.
    /// </summary>
    public static string BuildTargetDirectory(
        string targetRoot, string? baseModelRaw, string? categoryFolderName, bool includeCategory)
        => BuildTargetDirectory(targetRoot, baseModelRaw, categoryFolderName, includeBaseModel: true, includeCategory);

    /// <summary>
    /// Same directory construction, with the base-model segment itself optional. When
    /// <paramref name="includeBaseModel"/> is false, no base-model segment is added — not even the
    /// <see cref="UnknownFolderName"/> fallback — which is what a picker whose "create base model
    /// folder" toggle is off needs: previously the download pickers built this path by hand and
    /// simply omitted the segment for a blank base model while leaving it toggle-gated for a real
    /// one, so turning the toggle ON with an unresolved base model silently produced no segment at
    /// all instead of the sorter's <c>Unknown\</c> bucket — see spec §4.4.
    /// </summary>
    public static string BuildTargetDirectory(
        string targetRoot, string? baseModelRaw, string? categoryFolderName,
        bool includeBaseModel, bool includeCategory)
    {
        var path = targetRoot;
        if (includeBaseModel)
        {
            var baseFolder = IsPlaceholderBaseModel(baseModelRaw)
                ? UnknownFolderName
                : SanitizeFolderName(baseModelRaw!);
            path = Path.Combine(path, baseFolder);
        }
        if (includeCategory && !IsUnresolvedCategory(categoryFolderName))
            path = Path.Combine(path, SanitizeFolderName(categoryFolderName!));
        return path;
    }

    /// <summary>Null, empty, or the Unknown bucket name — i.e. "no category segment".</summary>
    public static bool IsUnresolvedCategory(string? categoryFolderName)
        => string.IsNullOrWhiteSpace(categoryFolderName)
           || string.Equals(categoryFolderName.Trim(), UnknownFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a support asset goes: a flat, per-kind folder directly under the target root, beside
    /// the base-model folders (#527). No base-model segment and no category segment — both answer
    /// questions about a LoRA's provenance, and neither means anything for a VAE.
    /// </summary>
    /// <remarks>
    /// The folder name comes from <see cref="ModelTypeExtensions.SupportFolderName"/>, which is the
    /// same string the preview's chip shows, so the tree can never advertise a folder the sorter
    /// does not create. Throws for a non-support kind rather than inventing a folder: a LoRA's
    /// destination is its base model, and reaching here with one is a caller bug.
    /// </remarks>
    public static string BuildSupportAssetDirectory(string targetRoot, ModelType kind)
    {
        var folder = kind.SupportFolderName()
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Only a support asset has a per-kind folder; a LoRA's folder is its base model.");
        return Path.Combine(targetRoot, SanitizeFolderName(folder));
    }

    /// <summary>
    /// The naming sequence a colliding file walks: the plain name, then <c>{stem}_{versionId}</c>
    /// when there is a version id, then <c>{stem}_2</c>, <c>_3</c>, … without end.
    /// </summary>
    /// <remarks>
    /// Name selection only — deliberately no "is it taken" callback and no disk access. Choosing a
    /// name needs a content comparison at every step (a taken name holding an identical file means
    /// "already sorted, skip", not "try the next name"), and the hashes and the plan-local claim
    /// map both live in <see cref="LoraSortPlanner"/>. Splitting that decision across two types is
    /// what let the numeric fallback grow <c>_2</c>, <c>_3</c>, <c>_4</c>… on every re-run of a copy:
    /// each run collided on the plain name, saw its own previous copy as merely "taken", and never
    /// compared content with it.
    /// </remarks>
    public static IEnumerable<string> EnumerateCandidateNames(string fileName, int? civitaiVersionId)
    {
        yield return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        if (civitaiVersionId is { } versionId)
            yield return $"{stem}_{versionId}{extension}";

        for (var i = 2; ; i++)
            yield return $"{stem}_{i}{extension}";
    }
}
