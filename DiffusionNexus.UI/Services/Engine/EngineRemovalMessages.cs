using System.Globalization;
using System.Text;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>The user's answer to a removal question.</summary>
public enum RemovalChoice { Cancel, EngineOnly, Everything }

/// <summary>
/// Text and buttons for removing a feature and uninstalling the Engine, shown through
/// <c>IDialogService.ShowOptionsAsync</c>: Cancel first, the safe choice last (primary).
/// </summary>
public static class EngineRemovalMessages
{
    public const string FeatureTitle = "Remove Engine feature";
    public const string UninstallTitle = "Uninstall the Diffusion Nexus Engine";
    private const string Cancel = "Cancel";
    private const string EngineOnly = "Delete only what's in the Diffusion Nexus Engine";

    public static string[] FeatureOptions(bool hasOutside) => hasOutside
        ? [Cancel, "Delete all models of this feature, wherever they are", EngineOnly]
        : [Cancel, "Remove"];

    public static string[] UninstallOptions(bool hasOutside) => hasOutside
        ? [Cancel, "Delete these models too", EngineOnly]
        : [Cancel, "Uninstall"];

    /// <summary>Maps the dialog's button index (-1 = closed) back to a choice.</summary>
    public static RemovalChoice ToChoice(int index, bool hasOutside) => (hasOutside, index) switch
    {
        (false, 1) => RemovalChoice.EngineOnly,
        (true, 1) => RemovalChoice.Everything,
        (true, 2) => RemovalChoice.EngineOnly,
        _ => RemovalChoice.Cancel
    };

    public static string FeatureRemoval(string featureName, EngineRemovalPlan plan)
    {
        var text = new StringBuilder($"Remove {featureName}?");
        text.Append(plan.NodePackFolders.Count == 0 && plan.EngineModels.Count == 0
            ? "\n\nNothing of it is in the Diffusion Nexus Engine's own folder."
            : $"\n\nThis deletes {Count(plan.NodePackFolders.Count, "custom node pack")} and " +
              $"{Count(plan.EngineModels.Count, "model")} ({FormatSize(plan.EngineBytes)}) from the Diffusion Nexus Engine.");

        foreach (var name in plan.RemovedWithIt)
            text.Append($"\n\n{name} uses only files of this feature, so it is removed too.");

        if (plan.Kept.Count > 0)
        {
            text.Append("\n\nKept, because another installed feature needs it:");
            foreach (var item in plan.Kept)
                text.Append($"\n• {item.Name} (used by {item.NeededBy})");
        }

        AppendOutside(text, "Some of this feature's models are in your own model folders.", plan.OutsideModels);
        text.Append("\n\nPython packages the feature added stay in the Engine's environment; Uninstall Engine removes them.");
        return text.ToString();
    }

    public static string NothingToRemove(string featureName, EngineRemovalPlan plan)
    {
        var text = new StringBuilder($"Nothing of {featureName} can be deleted: every file it uses is needed by another installed feature.");
        foreach (var item in plan.Kept)
            text.Append($"\n• {item.Name} (used by {item.NeededBy})");
        return text.ToString();
    }

    /// <summary>Other features whose workloads the catalog lacks: which files they share is unknown, so nothing is removed.</summary>
    public static string CatalogLacks(IReadOnlyList<string> featureNames)
    {
        var names = featureNames.Count == 1
            ? featureNames[0]
            : string.Join(", ", featureNames.Take(featureNames.Count - 1)) + " and " + featureNames[^1];
        var sharer = featureNames.Count == 1 ? $"{names} shares" : "they share";
        return $"The catalog has no description of {names}, so the app cannot tell which files {sharer}. " +
               "Update the catalog or switch its channel, then try again. Uninstall Engine still works.";
    }

    public static string CheckFailed(string featureName, string reason) =>
        $"The files of {featureName} could not be checked ({reason}), so nothing was removed. " +
        "Try again; the Unified Console has the details.";

    /// <param name="unclaimed">
    /// Model files in the Engine's models folder that no feature declares; null when the feature check
    /// failed and they are not known.
    /// </param>
    public static string Uninstall(string folder, long folderBytes, IReadOnlyList<PlannedFile> outside, ModelFileTotal? unclaimed = null)
    {
        var text = new StringBuilder("Uninstall the Diffusion Nexus Engine?");
        text.Append($"\n\nThis deletes {folder} ({FormatSize(folderBytes)}): the Engine, its features and the models it downloaded.");
        if (outside.Count == 0)
            text.Append(" Your own model folders are not touched.");
        if (unclaimed is { Count: > 0 })
            text.Append($"\n\nIt also holds {Count(unclaimed.Count, "other model file")} ({FormatSize(unclaimed.Bytes)}) that no Engine feature uses, " +
                        "for example ones another part of the app downloaded there. They are deleted with the Engine.");
        AppendOutside(text, "Some models the Engine's features use are in your own model folders.", outside);
        return text.ToString();
    }

    /// <summary>
    /// The question when the Engine folder is itself a junction or symlink: only the link goes, so no size
    /// and no count of the files behind it (none of them is deleted).
    /// </summary>
    public static string UninstallLink(string folder, string? target, IReadOnlyList<PlannedFile> outside)
    {
        var text = new StringBuilder("Uninstall the Diffusion Nexus Engine?");
        text.Append($"\n\nIts folder {folder} is a link to {target ?? "another folder"}, so only the link is removed: " +
                    $"the files it points to stay {(target is null ? "where they are" : $"in {target}")}.");
        if (outside.Count == 0)
            text.Append(" Your own model folders are not touched.");
        AppendOutside(text, "Some models the Engine's features use are in your own model folders.", outside);
        return text.ToString();
    }

    /// <summary>The Engine folder was itself a junction or symlink: only the link went, its target's files stay.</summary>
    public static string UninstalledLinkOnly(string folder, string? target) =>
        $"The Diffusion Nexus Engine was uninstalled. Its folder {folder} was a link to another folder, so only the link was removed: " +
        $"the files behind it stay in {target ?? "the folder it pointed to"}. Delete them there if you no longer need them.";

    public static string UninstallMissingFolder(string folder) =>
        $"Uninstall the Diffusion Nexus Engine?\n\nIts folder {folder} is already gone. The app forgets the Engine and its model folder in Settings.";

    public static string UninstallUnsafeFolder(string folder) =>
        $"Uninstall the Diffusion Nexus Engine?\n\nIts recorded folder \"{folder}\" is not a folder the app can safely delete, " +
        "so nothing on disk is touched. The app only forgets the Engine; the folders listed in Settings stay as they are.";

    public static string Busy(string reason) =>
        $"The Diffusion Nexus Engine is busy ({reason}). Wait until it finishes, then try again.";

    public static string Failures(string headline, IReadOnlyList<RemovalFailure> failures)
    {
        var text = new StringBuilder(headline);
        text.Append("\n\nCould not be deleted:");
        foreach (var failure in failures)
            text.Append($"\n• {failure.Path} — {failure.Reason}");
        return text.ToString();
    }

    public static string FormatSize(long bytes)
    {
        const double gb = 1024d * 1024 * 1024, mb = 1024d * 1024;
        return bytes >= gb
            ? (bytes / gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : Math.Round(bytes / mb).ToString(CultureInfo.InvariantCulture) + " MB";
    }

    private static void AppendOutside(StringBuilder text, string lead, IReadOnlyList<PlannedFile> outside)
    {
        if (outside.Count == 0) return;
        text.Append($"\n\n{lead} Other programs (ComfyUI, A1111, …) may use them too:");
        foreach (var file in outside)
            text.Append($"\n• {file.Path} ({FormatSize(file.Bytes)})");
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}

/// <summary>A path a removal could not delete, and why.</summary>
public sealed record RemovalFailure(string Path, string Reason);
