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
    /// <summary>
    /// The settings rows associated with one installation: the three removable sets, plus
    /// the paths held back per kind because another installation still uses them. The
    /// held-back paths are tracked per kind, not as one flat list, so the dialog can label
    /// each row correctly — a gallery that exists but is shared must not read "none linked".
    /// </summary>
    public sealed record Plan(
        IReadOnlyList<ImageGallery> Galleries,
        IReadOnlyList<LoraSource> LoraSources,
        IReadOnlyList<BaseModelFolder> BaseModelFolders,
        IReadOnlyList<string> SharedGalleryFolders,
        IReadOnlyList<string> SharedLoraSourceFolders,
        IReadOnlyList<string> SharedBaseModelFolders)
    {
        /// <summary>True when the installation has no removable settings rows at all.</summary>
        public bool IsEmpty => Galleries.Count == 0 && LoraSources.Count == 0 && BaseModelFolders.Count == 0;

        /// <summary>Every held-back path across the three kinds, de-duplicated.</summary>
        public IReadOnlyList<string> SharedFolders =>
        [
            .. SharedGalleryFolders
                .Concat(SharedLoraSourceFolders)
                .Concat(SharedBaseModelFolders)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
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
    /// <param name="ownOutputFolders">
    /// This installation's own resolved output folders. A gallery row for one of them
    /// belongs to this installation even when the FK says otherwise — re-adding an
    /// installation leaves the row's FK behind on the old package id, and reporting
    /// "none linked" for a gallery the user actively uses is simply false. Such a row is
    /// only ever reported as kept, never swept, because the FK names its owner.
    /// </param>
    public static Plan Resolve(
        AppSettings settings,
        InstallerPackage package,
        IReadOnlyCollection<string>? protectedRoots = null,
        IReadOnlyCollection<string>? foldersUsedByOthers = null,
        IReadOnlyCollection<string>? ownOutputFolders = null)
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

        var sharedGalleries = new List<string>();
        var sharedLoraSources = new List<string>();
        var sharedBaseModelFolders = new List<string>();

        // True when the row may be swept; records the path in <paramref name="shared"/>
        // when another installation's continued use means it has to stay.
        bool IsRemovable(string? folderPath, List<string> shared)
        {
            if (!otherUsage.Any(used => IsUnderInstallation(folderPath, used)))
            {
                return true;
            }

            shared.Add(folderPath!);
            return false;
        }

        var ownOutputs = (ownOutputFolders ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        // A gallery for one of this installation's own output folders that carries a
        // DIFFERENT package's FK: really this installation's, but owned on paper by
        // another one, so it is reported as kept rather than offered or denied.
        var claimedElsewhere = settings.ImageGalleries
            .Where(g => g.InstallerPackageId is not null && g.InstallerPackageId != package.Id)
            .Where(g => ownOutputs.Any(own => IsUnderInstallation(g.FolderPath, own)))
            .Select(g => g.FolderPath)
            .ToList();

        var galleries = settings.ImageGalleries
            .Where(g => g.InstallerPackageId == package.Id
                        || (g.InstallerPackageId is null
                            && ownOutputs.Any(own => IsUnderInstallation(g.FolderPath, own))))
            .Where(g => IsRemovable(g.FolderPath, sharedGalleries))
            .ToList();

        sharedGalleries.AddRange(claimedElsewhere);

        var loraSources = settings.LoraSources
            .Where(s => IsUnderInstallation(s.FolderPath, package.InstallationPath))
            .Where(s => IsRemovable(s.FolderPath, sharedLoraSources))
            .ToList();

        var baseModelFolders = settings.BaseModelFolders
            .Where(f => f.InstallerPackageId == package.Id
                        || (f.InstallerPackageId is null
                            && IsUnderInstallation(f.FolderPath, package.InstallationPath)))
            .Where(f => NormalizePath(f.FolderPath) is not { } normalized
                        || !protectedNormalized.Contains(normalized))
            .Where(f => IsRemovable(f.FolderPath, sharedBaseModelFolders))
            .ToList();

        return new Plan(
            galleries,
            loraSources,
            baseModelFolders,
            Distinct(sharedGalleries),
            Distinct(sharedLoraSources),
            Distinct(sharedBaseModelFolders));

        static IReadOnlyList<string> Distinct(List<string> paths) =>
            [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Full, trailing-separator-free form of a path; null when invalid.</summary>
    private static string? NormalizePath(string? path) => FolderPathMatch.Normalize(path);

    /// <summary>
    /// True when <paramref name="folderPath"/> equals or lies underneath
    /// <paramref name="installationPath"/> (case-insensitive, separator-tolerant).
    /// Invalid paths never match.
    /// </summary>
    internal static bool IsUnderInstallation(string? folderPath, string? installationPath) =>
        FolderPathMatch.Contains(installationPath, folderPath);
}
