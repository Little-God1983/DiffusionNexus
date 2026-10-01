namespace DiffusionNexus.UI.ImageEditor;

public partial class ImageEditorCore
{
    /// <summary>The file <see cref="LoadImage(string)"/> decoded, as it was on disk at the time.</summary>
    private FileStamp? _loadedFile;

    /// <summary>The file the canvas currently equals, or null once it has been edited.</summary>
    private FileStamp? _matchedFile;

    /// <summary>
    /// True while the canvas is exactly the file at <see cref="CurrentImagePath"/>: unedited since
    /// it was loaded or reset, or since the last save over that file (#586). This is not the
    /// inverse of <see cref="IsDirty"/>: Export and Save as New clear the dirty flag while the
    /// canvas still differs from the file it was loaded from.
    /// </summary>
    /// <remarks>
    /// Does not look at the disk or at pending tool work; <see cref="GetUnchangedFilePath"/> does.
    /// </remarks>
    public bool MatchesFile => _matchedFile is not null;

    /// <summary>Raised when <see cref="MatchesFile"/> flips.</summary>
    public event EventHandler? MatchesFileChanged;

    /// <summary>
    /// The file another tool can be given instead of a re-encoded copy of the canvas, or null when
    /// the canvas differs from it. Besides <see cref="MatchesFile"/>, nothing may be pending (a
    /// save would commit placed text or an open Move first), and the file must still be the one
    /// that was loaded or saved: same size and write time.
    /// </summary>
    public string? GetUnchangedFilePath()
    {
        if (_matchedFile is not { } matched || HasPendingOperations)
            return null;

        return matched.IsCurrent() ? matched.Path : null;
    }

    /// <summary>
    /// Declares the canvas equal to <paramref name="filePath"/>, which a save has just written in
    /// full. Only for a save over the loaded file without a transparency fill: a filled JPEG holds
    /// a colour where the canvas is transparent.
    /// </summary>
    public void MarkSavedOverFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || CurrentImagePath is null
            || !string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(CurrentImagePath), StringComparison.OrdinalIgnoreCase))
            return;

        // The file now holds this canvas, so Reset would no longer restore it.
        _loadedFile = null;
        SetMatchedFile(FileStamp.TryCapture(filePath));
    }

    /// <summary>Work a save would commit before encoding, which therefore is not in the file.</summary>
    private bool HasPendingOperations =>
        TextTool.HasPlacedText
        || ShapeTool.HasPlacedShape || ShapeTool.IsDrawing
        || DrawingTool.IsDrawing
        || (LayerTransformTool.IsArmed && (LayerTransformTool.HasTransform || LayerTransformTool.IsDragging));

    /// <summary>After a successful load from <paramref name="filePath"/>.</summary>
    private void OnLoadedFromFile(string filePath)
    {
        _loadedFile = FileStamp.TryCapture(filePath);
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
        var wasMatched = MatchesFile;
        _matchedFile = value;
        if (wasMatched != MatchesFile)
            MatchesFileChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Identifies one version of a file: a rewrite changes its write time or size.</summary>
    private sealed record FileStamp(string Path, long Length, DateTime LastWriteTimeUtc)
    {
        public static FileStamp? TryCapture(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileStamp(info.FullName, info.Length, info.LastWriteTimeUtc) : null;
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
