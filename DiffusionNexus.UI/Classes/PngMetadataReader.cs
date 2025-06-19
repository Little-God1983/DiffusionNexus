using System;
using System.Globalization;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;

namespace DiffusionNexus.UI.Classes
{
    public static class PngMetadataReader
    {
        public static StableDiffusionMetadata? ReadMetadata(string path)
        {
            using var stream = File.OpenRead(path);
            var info = Image.Identify(stream);
            var format = info?.Metadata.DecodedImageFormat;
            if (format is not PngFormat)
                return null;

            stream.Position = 0; // Reset stream position for loading
            using var image = Image.Load(stream);
            var pngMeta = image.Metadata.GetPngMetadata();
            var textData = pngMeta.TextData.FirstOrDefault(t => t.Keyword == "parameters");
            if (textData == null)
                return null;
            return Parse(textData.Value);
        }

        private static StableDiffusionMetadata Parse(string input)
        {
            // Return an empty/default metadata if there's nothing to parse
            if (string.IsNullOrWhiteSpace(input))
                return new StableDiffusionMetadata();

            var meta = new StableDiffusionMetadata();
            var lines = input.Split('\n');
            if (lines.Length > 0)
                meta.Prompt = lines[0].Replace("Prompt:", string.Empty).Trim();
            if (lines.Length > 1)
                meta.NegativePrompt = lines[1].Replace("Negative prompt:", string.Empty).Trim();

            for (int i = 2; i < lines.Length; i++)
            {
                foreach (var part in lines[i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(new[] { ':', '=' }, 2);
                    if (kv.Length != 2) continue;
                    var key = kv[0].Trim();
                    var value = kv[1].Trim();
                    var keyLower = key.ToLowerInvariant();

                    switch (keyLower)
                    {
                        case "steps":
                            if (int.TryParse(value, out var steps)) meta.Steps = steps;
                            break;
                        case "sampler":
                            meta.Sampler = value;
                            break;
                        case "schedule type":
                            meta.ScheduleType = value;
                            break;
                        case "cfg scale":
                            if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var cfg)) meta.CFGScale = cfg;
                            break;
                        case "seed":
                            if (long.TryParse(value, out var seed)) meta.Seed = seed;
                            break;
                        case "face restoration":
                            meta.FaceRestoration = value;
                            break;
                        case "size":
                            var dims = value.Split('x');
                            if (dims.Length == 2)
                            {
                                int.TryParse(dims[0], out var w);
                                int.TryParse(dims[1], out var h);
                                meta.Width = w;
                                meta.Height = h;
                            }
                            break;
                        case "model hash":
                            meta.ModelHash = value;
                            break;
                        case "model":
                            meta.Model = value;
                            break;
                        case "ti":
                            meta.TI = value;
                            break;
                        case "version":
                            meta.Version = value;
                            break;
                        case "source identifier":
                            meta.SourceIdentifier = value;
                            break;
                        case "lora hashes":
                            meta.LoRAHashes = value;
                            break;
                        case "width":
                            if (int.TryParse(value, out var width)) meta.Width = width;
                            break;
                        case "height":
                            if (int.TryParse(value, out var height)) meta.Height = height;
                            break;
                        case "hashes":
                            meta.Hashes = value;
                            break;
                        case "resources":
                            meta.Resources = value;
                            break;
                    }
                }
            }
            return meta;
        }
    }
}

