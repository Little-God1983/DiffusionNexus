using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Resolves which settings rows belong to an installation that is being removed from
/// the Installer Manager, so the remove dialog can offer to clean them up:
/// Generation Galleries are matched by their <c>InstallerPackageId</c> FK (the add flow
/// always links them, and an output folder can live anywhere on disk); LoRA sources
/// have no FK and are matched by living underneath the installation path; Base Model
/// Folders match by this package's FK <em>or</em> by unclaimed path (FK null) — rows
/// registered for a DIFFERENT installation are never swept, and neither are
/// <c>protectedRoots</c>: model roots another registered installation
/// still discovers (their startup backfill would silently resurrect the row,
/// contradicting the dialog's "no longer scanned" promise).
///
/// Rows are likewise held back when they sit in a folder another surviving installation
/// still uses (<c>foldersUsedByOthers</c>) — several ComfyUI installations routinely share
/// one <c>--output-directory</c> while only one of them owns the gallery row's FK, so
/// ownership by FK is not the same as exclusive use. Those held-back paths are reported in
/// <see cref="Plan.SharedFolders"/> so the dialog can say why they are being kept instead
/// of silently claiming nothing was linked.
/// </summary>
public static class InstallationSettingsCleanup
{
    /// <summary>The settings rows associated with one installation.</summary>
    public sealed record Plan(
        IReadOnlyList<ImageGallery> Galleries,
        IReadOnlyList<LoraSource> LoraSources,
        IReadOnlyList<BaseModelFolder> BaseModelFolders,
        IReadOnlyList<string> SharedFolders)
    {
        /// <summary>True when the installation has no removable settings rows at all.</summary>
        public bool IsEmpty => Galleries.Count == 0 && LoraSources.Count == 0 && BaseModelFolders.Count == 0;
    }

    /// <summary>Resolves the rows tied to <paramref name="package"/>.</summary>
    /// <param name="settings">Settings graph including the three folder collections.</param>
    /// <param name="package">The installation being removed.</param>
    /// <param name="protectedRoots">
    /// Model roots still discoverable from OTHER registered installations
    /// (<see cref="Diffusion.BaseModelFolderRegistrar.ResolveModelRoots"/> over the
    /// remaining packages); base folder rows on exactly these paths are kept, because
    /// the other installation's startup backfill would re-add them anyway. Matched
    /// exactly, not by containment: a row nested inside a discovered root is a root of
    /// its own that no backfill would resurrect.
    /// </param>
    /// <param name="foldersUsedByOthers">
    /// Folders the surviving installations still read or write — their installation
    /// paths and their resolved <see cref="InstallationOutputFolderResolver"/> output
    /// directories. Matched by containment (a row at or below such a folder is kept),
    /// since ComfyUI writes into dated subfolders of its output directory.
    /// </param>
    public static Plan Resolve(
        AppSettings settings,
        InstallerPackage package,
        IReadOnlyCollection<string>? protectedRoots = null,
        IReadOnlyCollection<string>? foldersUsedByOthers = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(package);

        var protectedNormalized = (protectedRoots ?? [])
            .Select(NormalizePath)
            .Where(p => p is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var otherUsage = (foldersUsedByOthers ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var shared = new List<string>();

        // True when the row may be swept; records the path as shared when it may not.
        bool IsRemovable(string? folderPath)
        {
            if (!otherUsage.Any(used => IsUnderInstallation(folderPath, used)))
            {
                return true;
            }

            shared.Add(folderPath!);
            return false;
        }

        var galleries = settings.ImageGalleries
            .Where(g => g.InstallerPackageId == package.Id)
            .Where(g => IsRemovable(g.FolderPath))
            .ToList();

        var loraSources = settings.LoraSources
            .Where(s => IsUnderInstallation(s.FolderPath, package.InstallationPath))
            .Where(s => IsRemovable(s.FolderPath))
            .ToList();

        var baseModelFolders = settings.BaseModelFolders
            .Where(f => f.InstallerPackageId == package.Id
                        || (f.InstallerPackageId is null
                            && IsUnderInstallation(f.FolderPath, package.InstallationPath)))
            .Where(f => NormalizePath(f.FolderPath) is not { } normalized
                        || !protectedNormalized.Contains(normalized))
            .Where(f => IsRemovable(f.FolderPath))
            .ToList();

        return new Plan(
            galleries,
            loraSources,
            baseModelFolders,
            shared.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Full, trailing-separator-free form of a path; null when invalid.</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="folderPath"/> equals or lies underneath
    /// <paramref name="installationPath"/> (case-insensitive, separator-tolerant).
    /// Invalid paths never match.
    /// </summary>
    internal static bool IsUnderInstallation(string? folderPath, string? installationPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(installationPath))
        {
            return false;
        }

        string candidate;
        string root;
        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationPath));
        }
        catch (Exception)
        {
            return false;
        }

        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
