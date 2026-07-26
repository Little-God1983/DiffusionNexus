using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// A selectable model storage root shown in download-target dropdowns.
/// </summary>
/// <param name="Path">Absolute folder path.</param>
/// <param name="IsDefault">Whether this is the default download target.</param>
/// <param name="Exists">Whether the directory currently exists on disk.</param>
public sealed record ModelFolderOption(string Path, bool IsDefault, bool Exists)
{
    /// <summary>Dropdown display text.</summary>
    public string Display => IsDefault ? $"{Path} (default)" : Path;
}

/// <summary>
/// Resolution authority for the Base Model Folders registry: which folders a user can
/// download Diffusion Nexus Core models into (and which one is the default), and which
/// folders are scanned when looking models up. The registered ComfyUI installations'
/// models folders are NOT part of this catalog — <c>LocalDiffusionBackendProvider</c>
/// appends those separately so detection stays a superset of the pre-registry behavior.
/// </summary>
public interface IModelFolderCatalog
{
    /// <summary>
    /// Dropdown items: enabled Base Model Folders with the default first; when none are
    /// configured, exactly one entry — the LocalAppData fallback — flagged as default.
    /// </summary>
    Task<IReadOnlyList<ModelFolderOption>> GetDownloadTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The default download root (first download target). The directory is created on
    /// demand; when creation fails the LocalAppData fallback is returned instead
    /// (logged as a warning).
    /// </summary>
    Task<string> GetDefaultDownloadRootAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Folders to scan when looking up models: enabled Base Model Folders plus the
    /// LocalAppData fallback — deduped case-insensitively, existing directories only.
    /// </summary>
    Task<IReadOnlyList<string>> GetSearchRootsAsync(CancellationToken cancellationToken = default);
}
