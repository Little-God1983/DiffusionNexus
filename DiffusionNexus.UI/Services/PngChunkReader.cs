using System.Text;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Reads PNG tEXt and iTXt chunks without any image processing library.
/// PNG spec: each chunk = 4-byte length (big-endian) + 4-byte type + data + 4-byte CRC.
/// </summary>
internal static class PngChunkReader
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Extracts all tEXt and iTXt text chunks from a PNG file.
    /// </summary>
    public static Dictionary<string, string> ReadTextChunks(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        // Validate PNG signature (8 bytes)
        var sig = reader.ReadBytes(PngSignature.Length);
        if (sig.Length < PngSignature.Length || !sig.AsSpan().SequenceEqual(PngSignature))
        {
            return result;
        }

        while (stream.Position < stream.Length - 8)
        {
            var lengthBytes = reader.ReadBytes(4);
            if (lengthBytes.Length < 4) break;

            if (BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);
            int length = BitConverter.ToInt32(lengthBytes, 0);

            var typeBytes = reader.ReadBytes(4);
            if (typeBytes.Length < 4) break;

            var type = Encoding.ASCII.GetString(typeBytes);

            // Stop at IEND chunk
            if (type == "IEND") break;

            if (length > 0 && type is "tEXt" or "iTXt")
            {
                var data = reader.ReadBytes(length);
                if (data.Length == length)
                {
                    ParseTextChunk(type, data, result);
                }
            }
            else
            {
                // Skip data we don't need
                if (stream.Position + length > stream.Length) break;
                stream.Seek(length, SeekOrigin.Current);
            }

            // Skip CRC (4 bytes)
            if (stream.Position + 4 > stream.Length) break;
            stream.Seek(4, SeekOrigin.Current);
        }

        return result;
    }

    private static void ParseTextChunk(string type, byte[] data, Dictionary<string, string> result)
    {
        if (type == "tEXt")
        {
            int nullIdx = Array.IndexOf(data, (byte)0);
            if (nullIdx < 0) return;

            var key = Encoding.Latin1.GetString(data, 0, nullIdx);
            var val = Encoding.Latin1.GetString(data, nullIdx + 1, data.Length - nullIdx - 1);
            result[key] = val;
        }
        else if (type == "iTXt")
        {
            int nullIdx = Array.IndexOf(data, (byte)0);
            if (nullIdx < 0) return;

            var key = Encoding.UTF8.GetString(data, 0, nullIdx);
            int pos = nullIdx + 1;

            // Skip compression flag + compression method (2 bytes)
            if (pos + 2 > data.Length) return;
            pos += 2;

            // Skip language tag (null-terminated)
            int langEnd = Array.IndexOf(data, (byte)0, pos);
            if (langEnd < 0) return;
            pos = langEnd + 1;

            // Skip translated keyword (null-terminated)
            int transEnd = Array.IndexOf(data, (byte)0, pos);
            if (transEnd < 0) return;
            pos = transEnd + 1;

            var val = Encoding.UTF8.GetString(data, pos, data.Length - pos);
            result[key] = val;
        }
    }
}
