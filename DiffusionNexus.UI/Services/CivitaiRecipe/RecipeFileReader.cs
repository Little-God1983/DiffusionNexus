namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>What an original Civitai file turned out to be, judged by its first bytes.</summary>
public enum RecipeFileFormat
{
    Unknown,
    Png,
    Jpeg,
    WebP,
    Video,
}

/// <summary>
/// The generation texts found in an original file, under the PNG chunk names: <c>prompt</c> (the
/// ComfyUI API graph), <c>workflow</c> (the editor graph) and <c>parameters</c> (A1111/Forge text —
/// for a JPEG, its EXIF UserComment).
/// </summary>
internal sealed record RecipeFileText(RecipeFileFormat Format, IReadOnlyDictionary<string, string> Texts);

/// <summary>
/// Reads the generation texts of a downloaded Civitai original (#628). The format comes from the
/// magic bytes, never the name: Civitai serves the example image's PNG under a <c>….jpeg</c> URL.
/// WebP and video files are recognised but not read (none of the sampled WebPs carried data).
/// </summary>
internal static class RecipeFileReader
{
    public static RecipeFileFormat DetectFormat(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return RecipeFileFormat.Png;
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8])) return RecipeFileFormat.Jpeg;
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return RecipeFileFormat.WebP;
        if (head.Length >= 8 && head[4..8].SequenceEqual("ftyp"u8)) return RecipeFileFormat.Video;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3])) return RecipeFileFormat.Video;
        return RecipeFileFormat.Unknown;
    }

    public static RecipeFileText Read(string filePath)
    {
        Span<byte> head = stackalloc byte[16];
        int read;
        using (var stream = File.OpenRead(filePath))
        {
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        var format = DetectFormat(head[..read]);
        IReadOnlyDictionary<string, string> texts = format switch
        {
            RecipeFileFormat.Png => PngChunkReader.ReadTextChunks(filePath),
            RecipeFileFormat.Jpeg when JpegExifReader.ReadUserComment(filePath) is { } comment
                => new Dictionary<string, string> { ["parameters"] = comment },
            _ => new Dictionary<string, string>(),
        };
        return new RecipeFileText(format, texts);
    }
}
