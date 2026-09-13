namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Outcome of <see cref="ImageEditorCore.AddLayersFromFiles"/>: how many files became layers and
/// which ones could not be decoded (missing, corrupt, or a format Skia cannot read such as TIFF).
/// </summary>
public sealed record LayerImportResult(int Added, IReadOnlyList<string> Failed);
