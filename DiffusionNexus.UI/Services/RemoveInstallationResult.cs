using System.Collections.Generic;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Input for the remove-installation dialog: the installation's display name plus the
/// settings folders linked to it (resolved via
/// <see cref="InstallationSettingsCleanup.Resolve"/>). Empty lists disable the
/// corresponding cleanup checkbox.
///
/// The <c>Shared*</c> lists carry the folders that belong to this installation but are
/// still used by another one, so they are kept rather than offered. They are per kind
/// because each drives its own checkbox label — a gallery that exists but is shared has
/// to read "kept", never "none linked".
/// </summary>
public sealed record RemoveInstallationPrompt(
    string InstallationName,
    IReadOnlyList<string> GalleryFolders,
    IReadOnlyList<string> LoraSourceFolders,
    IReadOnlyList<string> BaseModelFolders,
    IReadOnlyList<string>? SharedGalleryFolders = null,
    IReadOnlyList<string>? SharedLoraSourceFolders = null,
    IReadOnlyList<string>? SharedBaseModelFolders = null);

/// <summary>
/// Result of the remove-installation dialog: whether the user confirmed the removal
/// and which linked settings rows should be removed along with the installation.
/// </summary>
public sealed record RemoveInstallationResult(
    bool Confirmed,
    bool RemoveGalleries,
    bool RemoveLoraSources,
    bool RemoveBaseModelFolders)
{
    public static RemoveInstallationResult Cancelled() => new(false, false, false, false);
}
