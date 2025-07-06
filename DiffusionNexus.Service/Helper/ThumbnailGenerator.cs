using SkiaSharp;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DiffusionNexus.Service.Helper;

public static class ThumbnailGenerator
{
    public static async Task<string?> GenerateThumbnailAsync(string mediaPath)
    {
        try
        {
            var ext = Path.GetExtension(mediaPath).ToLowerInvariant();
            var output = Path.ChangeExtension(mediaPath, ".webp");

            if (ext == ".gif")
            {
                using var codec = SKCodec.Create(mediaPath);
                using var bitmap = SKBitmap.Decode(codec);
                var height = bitmap.Height * ThumbnailSettings.MaxWidth / bitmap.Width;
                using var resized = bitmap.Resize(new SKImageInfo(ThumbnailSettings.MaxWidth, height), SKFilterQuality.Medium);
                using var img = SKImage.FromBitmap(resized);
                File.WriteAllBytes(output, img.Encode(SKEncodedImageFormat.Webp, ThumbnailSettings.JpegQuality).ToArray());
            }
            else
            {
                FFmpeg.SetExecutablesPath("/usr/bin");
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
                var conversion = await FFmpeg.Conversions
                    .FromSnippet
                    .Snapshot(mediaPath, output, ThumbnailSettings.VideoProbePosition);
                conversion.AddParameter($"-vf scale={ThumbnailSettings.MaxWidth}:-1", ParameterPosition.PostInput);
                await conversion.Start();
            }

            return File.Exists(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
