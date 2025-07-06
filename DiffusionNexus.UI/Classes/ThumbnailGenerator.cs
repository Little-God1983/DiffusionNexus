using System;
using System.IO;
using System.Threading.Tasks;
using SkiaSharp;
using Xabe.FFmpeg;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.UI.Classes
{
    public static class ThumbnailGenerator
    {
        public static async Task<string?> GenerateThumbnailAsync(string mediaPath)
        {
            try
            {
                var outputWebp = Path.ChangeExtension(mediaPath, ".webp");

                if (File.Exists(outputWebp))
                {
                    using var check = SKBitmap.Decode(outputWebp);
                    if (check != null && check.Width == ThumbnailSettings.MaxWidth)
                        return outputWebp;
                }

                var ext = Path.GetExtension(mediaPath).ToLowerInvariant();
                if (ext == ".gif")
                {
                    using var codec = SKCodec.Create(mediaPath);
                    using var bitmap = SKBitmap.Decode(codec);
                    if (bitmap == null)
                        return null;
                    var height = bitmap.Height * ThumbnailSettings.MaxWidth / bitmap.Width;
                    using var resized = bitmap.Resize(new SKImageInfo(ThumbnailSettings.MaxWidth, height), SKFilterQuality.Medium);
                    if (resized == null)
                        return null;
                    using var img = SKImage.FromBitmap(resized);
                    using var data = img.Encode(SKEncodedImageFormat.Webp, ThumbnailSettings.JpegQuality);
                    if (data == null)
                        return null;
                    await File.WriteAllBytesAsync(outputWebp, data.ToArray());
                }
                else
                {
                    var conversion = await FFmpeg.Conversions
                        .FromSnippet
                        .Snapshot(mediaPath, outputWebp, ThumbnailSettings.VideoProbePosition);
                    conversion.AddParameter($"-vf scale={ThumbnailSettings.MaxWidth}:-1", ParameterPosition.PostInput);
                    await conversion.Start();
                }

                return File.Exists(outputWebp) ? outputWebp : null;
            }
            catch (Exception ex)
            {
                LogEventService.Instance.Publish(LogSeverity.Error, $"Thumbnail generation failed: {ex.Message}");
                return null;
            }
        }
    }
}

