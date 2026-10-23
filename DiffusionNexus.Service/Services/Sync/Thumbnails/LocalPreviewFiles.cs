namespace DiffusionNexus.Service.Services.Sync.Thumbnails;

/// <summary>
/// Local-disk preview file conventions shared by the thumbnail pipeline: sibling-file
/// discovery (the extension ladder duplicated between <c>SidecarMetadataApplier</c> and
/// <c>ModelTileViewModel</c>) and the <c>file://</c> URL scheme those two writers use.
/// </summary>
public static class LocalPreviewFiles
{
    /// <summary>
    /// The scheme prefix written for on-disk preview images. <c>file://C:\loras\a.png</c> is
    /// malformed by construction — the drive letter parses as a URI authority — so the prefix
    /// must always be stripped string-wise. Never round-trip via <c>new Uri(url).LocalPath</c>.
    /// </summary>
    public const string FileUrlPrefix = "file://";

    /// <summary>
    /// The scheme prefix written for user-uploaded thumbnails
    /// (<see cref="System.ArgumentException"/>-free synthetic rows). Never fetchable.
    /// </summary>
    /// <remarks>
    /// The declaration itself lives on <see cref="DiffusionNexus.Domain.Entities.ModelImage"/>:
    /// candidate selection has to exclude these rows too, and DataAccess cannot see this assembly.
    /// Kept here as the Service-side spelling so the ladder reads in one namespace.
    /// </remarks>
    public const string UserThumbnailScheme = Domain.Entities.ModelImage.UserThumbnailScheme;

    /// <summary>
    /// Sibling preview-file extension ladder, in probe order. First match wins.
    /// </summary>
    public static readonly string[] Extensions =
    [
        ".preview.png",
        ".preview.jpg",
        ".preview.jpeg",
        ".preview.webp",
        ".thumb.jpg",
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
    ];

    /// <summary>
    /// Probes <paramref name="modelFilePath"/>'s directory for a sibling preview file, trying
    /// <see cref="Extensions"/> in order against the model's base file name. Returns
    /// <c>null</c> when the directory is missing or nothing on the ladder exists.
    /// </summary>
    public static string? FindSibling(string modelFilePath)
    {
        var directory = Path.GetDirectoryName(modelFilePath);
        if (string.IsNullOrEmpty(directory)) return null;

        var baseName = Path.GetFileNameWithoutExtension(modelFilePath);

        foreach (var extension in Extensions)
        {
            var candidate = Path.Combine(directory, baseName + extension);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Strips <see cref="FileUrlPrefix"/> from <paramref name="url"/> string-wise (never via
    /// <c>Uri</c> parsing — the prefix produces malformed URIs on Windows paths). Returns
    /// <c>false</c> for null, non-<c>file://</c>, or otherwise-schemed URLs.
    /// </summary>
    public static bool TryGetLocalPath(string? url, out string path)
    {
        if (url is not null && url.StartsWith(FileUrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = url[FileUrlPrefix.Length..];
            return true;
        }

        path = string.Empty;
        return false;
    }
}
