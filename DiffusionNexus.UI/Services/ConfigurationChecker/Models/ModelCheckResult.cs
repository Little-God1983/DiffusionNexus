using DiffusionNexus.Installer.SDK.Models.Enums;

namespace DiffusionNexus.UI.Services.ConfigurationChecker.Models;

/// <summary>
/// Check result for a single model download.
/// </summary>
public sealed record ModelCheckResult
{
    /// <summary>Unique ID of the model download entity.</summary>
    public required Guid Id { get; init; }

    /// <summary>Display name of the model.</summary>
    public required string Name { get; init; }

    /// <summary>Whether at least one expected file was found on disk.</summary>
    public required bool IsInstalled { get; init; }

    /// <summary>
    /// When the model has VRAM-profiled download links, this indicates
    /// whether the check was scoped to a single quant variant.
    /// </summary>
    public bool IsVramProfileScoped { get; init; }

    /// <summary>
    /// The VRAM profile used for scoping, if any.
    /// </summary>
    public VramProfile? ScopedVramProfile { get; init; }

    /// <summary>
    /// Indicates the model is a placeholder that may be downloaded at runtime by the engine.
    /// Placeholder models count as installed for status purposes.
    /// </summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>
    /// True when the underlying <see cref="ModelDownload"/> has download links
    /// with VRAM profile variants, requiring the user to choose a target profile.
    /// </summary>
    public bool HasVramProfiles { get; init; }

    /// <summary>All paths that were searched.</summary>
    public required IReadOnlyList<string> SearchedPaths { get; init; }

    /// <summary>The path where the file was found, or empty if missing.</summary>
    public string FoundAtPath { get; init; } = string.Empty;
}
