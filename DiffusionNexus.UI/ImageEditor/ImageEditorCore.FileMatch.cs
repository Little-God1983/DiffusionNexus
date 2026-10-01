using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.ImageEditor;

public partial class ImageEditorCore
{
    /// <summary>The file <see cref="LoadImage(string)"/> decoded, as it was on disk at the time.</summary>
    private FileStamp? _loadedFile;

    /// <summary>The file the canvas currently equals, or null once it has been edited.</summary>
    private FileStamp? _matchedFile;

    /// <summary>
    /// The file the canvas is exactly: unedited since it was loaded or reset, or since the last save
    /// over that file (#586). Null once edited. This is not the inverse of <see cref="IsDirty"/>:
    /// Export and Save as New clear the dirty flag while the canvas still differs from the file it
    /// was loaded from.
    /// </summary>
    /// <remarks>
    /// Does not look at the disk or at pending tool work; <see cref="GetUnchangedFilePath"/> does.
    /// A failed load leaves it on the previous file, so callers compare it with the path they show.
    /// </remarks>
    public string? MatchedFilePath => _matchedFile?.Path;

    /// <summary>Raised when <see cref="MatchedFilePath"/> changes.</summary>
    public event EventHandler? MatchesFileChanged;

    /// <summary>
    /// The file another tool can be given instead of a re-encoded copy of the canvas, or null when
    /// the canvas differs from it. Besides <see cref="MatchedFilePath"/>, nothing may be pending
    /// (<see cref="HasPendingOperations"/>: a save would commit it first), and the file must still
    /// be the version that was loaded or saved: same size and write time.
    /// </summary>
    public string? GetUnchangedFilePath()
    {
        if (_matchedFile is not { } matched || HasPendingOperations)
            return null;

        return matched.IsCurrent() ? matched.Path : null;
    }

    /// <summary>
    /// Declares the canvas equal to <paramref name="filePath"/>, which a save has just written in
    /// full. Only for a save over the loaded file without a transparency fill (a filled JPEG holds a
    /// colour where the canvas is transparent), and only in a format the extension names: a save to
    /// ".tif" or another unknown extension writes PNG bytes, which the tools reached by a hand-off
    /// cannot rely on.
    /// </summary>
    public void MarkSavedOverFile(string filePath)
    {
        if (!FilePaths.AreSame(filePath, CurrentImagePath)) return;

        // The file now holds this canvas, so Reset would no longer restore it.
        _loadedFile = null;

        var writtenAsNamed = _services?.Document.TryGetFormatFromExtension(filePath, out _) == true;
        SetMatchedFile(writtenAsNamed ? FileStamp.TryCapture(filePath) : null);
    }

    /// <summary>After a successful load of the file version <paramref name="loaded"/>.</summary>
    private void OnLoadedFromFile(FileStamp? loaded)
    {
        _loadedFile = loaded;
        SetMatchedFile(_loadedFile);
    }

    /// <summary>After a load that no plain file on disk equals (bytes, a layered TIFF) or a clear.</summary>
    private void ForgetLoadedFile()
    {
        _loadedFile = null;
        SetMatchedFile(null);
    }

    /// <summary>After <see cref="ResetToOriginal"/>: the canvas is the loaded file again.</summary>
    private void OnResetToLoadedFile() => SetMatchedFile(_loadedFile);

    private void SetMatchedFile(FileStamp? value)
    {
        var previous = MatchedFilePath;
        _matchedFile = value;
        if (!string.Equals(previous, MatchedFilePath, StringComparison.Ordinal))
            MatchesFileChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Identifies one version of a file: a rewrite changes its write time or size.</summary>
    private sealed record FileStamp(string Path, long Length, DateTime LastWriteTimeUtc)
    {
        /// <summary>The version <paramref name="info"/> describes as last read; null when it does not exist.</summary>
        public static FileStamp? From(FileInfo info)
            => info.Exists ? new FileStamp(info.FullName, info.Length, info.LastWriteTimeUtc) : null;

        public static FileStamp? TryCapture(string path)
        {
            try
            {
                return From(new FileInfo(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        public bool IsCurrent() => TryCapture(Path) is { } now
            && now.Length == Length && now.LastWriteTimeUtc == LastWriteTimeUtc;
    }
}
