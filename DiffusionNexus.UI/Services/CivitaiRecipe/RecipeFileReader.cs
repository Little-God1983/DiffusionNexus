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
/// A PNG is read from its text chunks, else from its <c>eXIf</c> chunk; a JPEG from its EXIF.
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
            RecipeFileFormat.Png => ReadPng(filePath),
            RecipeFileFormat.Jpeg when JpegExifReader.ReadUserComment(filePath) is { } comment
                => new Dictionary<string, string> { ["parameters"] = comment },
            _ => new Dictionary<string, string>(),
        };
        return new RecipeFileText(format, texts);
    }

    /// <summary>
    /// The PNG's text chunks; without a <c>parameters</c> chunk, the UserComment of its <c>eXIf</c>
    /// chunk takes that place — Civitai image 147042061 (2026-10-27) carries its parameters only there.
    /// </summary>
    private static Dictionary<string, string> ReadPng(string filePath)
    {
        var texts = PngChunkReader.ReadTextChunks(filePath);
        if (!texts.ContainsKey("parameters")
            && ReadPngExifChunk(filePath) is { } tiff
            && JpegExifReader.ReadUserCommentFromTiff(tiff) is { } comment)
        {
            texts["parameters"] = comment;
        }
        return texts;
    }

    /// <summary>The data of the first <c>eXIf</c> chunk, or null. Image data chunks are skipped, not read.</summary>
    private static byte[]? ReadPngExifChunk(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);
        stream.Seek(8, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(header);
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
            var type = header[4..8];
            if (length < 0 || stream.Position + length > stream.Length) return null;
            if (type.SequenceEqual("eXIf"u8)) return reader.ReadBytes(length);
            if (type.SequenceEqual("IEND"u8)) return null;
            stream.Seek(length + 4, SeekOrigin.Current); // data + CRC
        }
        return null;
    }
}
