using System.Text.Json;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Parses ComfyUI generation metadata embedded in PNG image text chunks.
/// Walks the workflow graph JSON to extract prompts, sampler settings, checkpoints, and LoRAs.
/// </summary>
internal sealed class ImageMetadataParser
{
    private static readonly HashSet<string> SamplerNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "KSampler", "KSamplerAdvanced", "KSampler (Efficient)",
        "KSampler Adv. (Efficient)", "SamplerCustom",
        "SamplerCustomAdvanced", "BNK_TiledKSampler",
        "KSampler SDXL (Eff.)", "ImpactKSamplerBasicPipe",
        "FaceDetailer", "UltimateSDUpscale"
    };

    private static readonly HashSet<string> CheckpointNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CheckpointLoaderSimple", "CheckpointLoader",
        "Efficient Loader", "Eff. Loader SDXL",
        "unCLIPCheckpointLoader"
    };

    private static readonly HashSet<string> LoraNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LoraLoader", "LoraLoaderModelOnly", "LoRAStacker",
        "Efficient Loader"
    };

    private const int MaxTraceDepth = 15;

    /// <summary>
    /// Reads an image file and extracts ComfyUI generation data from its PNG metadata.
    /// </summary>
    public ImageGenerationData Parse(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var fileName = Path.GetFileName(filePath);

        try
        {
            var (width, height) = ReadPngDimensions(filePath);

            var chunks = PngChunkReader.ReadTextChunks(filePath);
            if (!chunks.TryGetValue("prompt", out var promptJson) || string.IsNullOrWhiteSpace(promptJson))
            {
                return new ImageGenerationData
                {
                    FileName = fileName,
                    Width = width,
                    Height = height,
                    HasData = false
                };
            }

            var graph = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(promptJson);
            if (graph is null)
            {
                return new ImageGenerationData
                {
                    FileName = fileName,
                    Width = width,
                    Height = height,
                    HasData = false
                };
            }

            return ParseGraph(graph, fileName, width, height);
        }
        catch (Exception ex)
        {
            return new ImageGenerationData
            {
                FileName = fileName,
                HasData = false,
                ParseError = ex.Message
            };
        }
    }

    private static ImageGenerationData ParseGraph(
        Dictionary<string, JsonElement> graph,
        string fileName,
        int width,
        int height)
    {
        string? positivePrompt = null;
        string? negativePrompt = null;
        string? checkpoint = null;
        string? samplerName = null;
        string? scheduler = null;
        int? steps = null;
        long? seed = null;
        double? cfg = null;
        double? denoise = null;
        var loras = new List<LoraInfo>();

        foreach (var (_, nodeElement) in graph)
        {
            if (!nodeElement.TryGetProperty("class_type", out var classTypeProp))
                continue;

            var classType = classTypeProp.GetString() ?? "";

            if (!nodeElement.TryGetProperty("inputs", out var inputs))
                continue;

            // Samplers
            if (IsSamplerNode(classType) && samplerName is null)
            {
                ExtractSamplerData(inputs, graph,
                    ref positivePrompt, ref negativePrompt, ref checkpoint,
                    ref samplerName, ref scheduler, ref steps, ref seed, ref cfg, ref denoise);
            }

            // Checkpoints
            if (checkpoint is null && CheckpointNodeTypes.Contains(classType))
            {
                if (inputs.TryGetProperty("ckpt_name", out var ckpt))
                    checkpoint = ckpt.GetString();
            }

            // LoRAs
            if (LoraNodeTypes.Contains(classType))
            {
                if (inputs.TryGetProperty("lora_name", out var loraName))
                {
                    var name = loraName.GetString() ?? "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        double strengthModel = 1.0;
                        double strengthClip = 1.0;

                        if (inputs.TryGetProperty("strength_model", out var sm) && sm.TryGetDouble(out var smVal))
                            strengthModel = smVal;
                        if (inputs.TryGetProperty("strength_clip", out var sc) && sc.TryGetDouble(out var scVal))
                            strengthClip = scVal;

                        loras.Add(new LoraInfo
                        {
                            Name = name,
                            StrengthModel = strengthModel,
                            StrengthClip = strengthClip
                        });
                    }
                }
            }
        }

        var hasData = positivePrompt is not null
                      || negativePrompt is not null
                      || checkpoint is not null
                      || samplerName is not null
                      || loras.Count > 0;

        return new ImageGenerationData
        {
            FileName = fileName,
            Width = width,
            Height = height,
            PositivePrompt = positivePrompt,
            NegativePrompt = negativePrompt,
            Checkpoint = checkpoint,
            Loras = loras,
            SamplerName = samplerName,
            Scheduler = scheduler,
            Steps = steps,
            Seed = seed,
            Cfg = cfg,
            Denoise = denoise,
            HasData = hasData
        };
    }

    private static bool IsSamplerNode(string classType)
    {
        return SamplerNodeTypes.Contains(classType)
               || classType.Contains("sampler", StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractSamplerData(
        JsonElement inputs,
        Dictionary<string, JsonElement> graph,
        ref string? positivePrompt,
        ref string? negativePrompt,
        ref string? checkpoint,
        ref string? samplerName,
        ref string? scheduler,
        ref int? steps,
        ref long? seed,
        ref double? cfg,
        ref double? denoise)
    {
        if (inputs.TryGetProperty("seed", out var seedEl) && seedEl.TryGetInt64(out var s))
            seed = s;
        else if (inputs.TryGetProperty("noise_seed", out var nseed) && nseed.TryGetInt64(out var ns))
            seed = ns;

        if (inputs.TryGetProperty("steps", out var stepsEl) && stepsEl.TryGetInt32(out var st))
            steps = st;

        if (inputs.TryGetProperty("cfg", out var cfgEl) && cfgEl.TryGetDouble(out var c))
            cfg = c;

        if (inputs.TryGetProperty("sampler_name", out var sn))
            samplerName = sn.GetString();

        if (inputs.TryGetProperty("scheduler", out var sch))
            scheduler = sch.GetString();

        if (inputs.TryGetProperty("denoise", out var den) && den.TryGetDouble(out var d))
            denoise = d;

        if (inputs.TryGetProperty("positive", out var pos))
            positivePrompt ??= TraceText(pos, graph, 0);

        if (inputs.TryGetProperty("negative", out var neg))
            negativePrompt ??= TraceText(neg, graph, 0);

        if (checkpoint is null && inputs.TryGetProperty("model", out var model))
            checkpoint ??= TraceModel(model, graph, 0);
    }

    /// <summary>
    /// Follows node references to find the actual prompt text.
    /// ComfyUI stores references as JSON arrays: [nodeId, outputIndex].
    /// </summary>
    private static string? TraceText(JsonElement value, Dictionary<string, JsonElement> graph, int depth)
    {
        if (depth > MaxTraceDepth) return null;

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            return null;

        var refId = value[0].ToString();
        if (!graph.TryGetValue(refId, out var refNode))
            return null;

        if (!refNode.TryGetProperty("class_type", out var ctProp))
            return null;

        var classType = ctProp.GetString() ?? "";

        if (!refNode.TryGetProperty("inputs", out var inputs))
            return null;

        // Try common text-carrying properties
        ReadOnlySpan<string> textKeys =
        [
            "text", "text_g", "text_l", "text_positive",
            "text_negative", "string", "value", "wildcard",
            "prompt", "instruction"
        ];

        foreach (var key in textKeys)
        {
            if (inputs.TryGetProperty(key, out var textVal))
            {
                var result = TraceText(textVal, graph, depth + 1);
                if (!string.IsNullOrEmpty(result))
                    return result;
            }
        }

        // String concatenation nodes
        if (classType.Contains("concat", StringComparison.OrdinalIgnoreCase)
            || classType.Contains("join", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new List<string>();
            foreach (var prop in inputs.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var part = TraceText(prop.Value, graph, depth + 1);
                if (!string.IsNullOrEmpty(part))
                    parts.Add(part);
            }

            if (parts.Count > 0) return string.Join(" ", parts);
        }

        // Conditioning combine/concat — follow sub-references
        if (classType.Contains("conditioning", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<string> condKeys = ["conditioning_1", "conditioning_2", "conditioning", "cond"];
            foreach (var key in condKeys)
            {
                if (inputs.TryGetProperty(key, out var condVal))
                {
                    var result = TraceText(condVal, graph, depth + 1);
                    if (!string.IsNullOrEmpty(result))
                        return result;
                }
            }
        }

        // Generic fallback: try common reference input keys on any unrecognized node
        // (e.g. FluxKontextMultiReferenceLatentMethod, custom Flux nodes, etc.)
        ReadOnlySpan<string> fallbackKeys =
        [
            "positive", "negative", "conditioning", "conditioning_1", "conditioning_2",
            "cond", "clip", "prompt", "text_encode"
        ];

        foreach (var key in fallbackKeys)
        {
            if (inputs.TryGetProperty(key, out var refVal)
                && refVal.ValueKind == JsonValueKind.Array
                && refVal.GetArrayLength() == 2)
            {
                var result = TraceText(refVal, graph, depth + 1);
                if (!string.IsNullOrEmpty(result) && !result.StartsWith("[unresolved:", StringComparison.Ordinal))
                    return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Follows model references back through LoRA loaders to find the checkpoint name.
    /// </summary>
    private static string? TraceModel(JsonElement value, Dictionary<string, JsonElement> graph, int depth)
    {
        if (depth > MaxTraceDepth) return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            return null;

        var refId = value[0].ToString();
        if (!graph.TryGetValue(refId, out var refNode))
            return null;

        if (!refNode.TryGetProperty("inputs", out var inputs))
            return null;

        if (inputs.TryGetProperty("ckpt_name", out var ckpt))
            return ckpt.GetString();
        if (inputs.TryGetProperty("unet_name", out var unet))
            return unet.GetString();

        ReadOnlySpan<string> modelKeys = ["model", "model1"];
        foreach (var key in modelKeys)
        {
            if (inputs.TryGetProperty(key, out var modelRef))
            {
                var result = TraceModel(modelRef, graph, depth + 1);
                if (result is not null) return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads width and height from the IHDR chunk of a PNG file without decoding pixels.
    /// </summary>
    private static (int Width, int Height) ReadPngDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            // Skip 8-byte PNG signature
            stream.Seek(8, SeekOrigin.Begin);

            // IHDR is always the first chunk
            var lengthBytes = reader.ReadBytes(4);
            if (BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);

            var typeBytes = reader.ReadBytes(4);
            var type = System.Text.Encoding.ASCII.GetString(typeBytes);
            if (type != "IHDR") return (0, 0);

            var widthBytes = reader.ReadBytes(4);
            var heightBytes = reader.ReadBytes(4);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(widthBytes);
                Array.Reverse(heightBytes);
            }

            return (BitConverter.ToInt32(widthBytes, 0), BitConverter.ToInt32(heightBytes, 0));
        }
        catch
        {
            return (0, 0);
        }
    }
}
