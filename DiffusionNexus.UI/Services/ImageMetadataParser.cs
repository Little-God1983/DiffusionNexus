using System.Text.Json;
using System.Text.RegularExpressions;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Parses image generation metadata embedded in PNG text chunks.
/// Supports ComfyUI (JSON workflow graph in "prompt" chunk) and
/// Automatic1111 / Forge UI (plain-text "parameters" chunk).
/// </summary>
internal sealed partial class ImageMetadataParser
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
    /// Reads an image file and extracts generation data from its PNG metadata.
    /// Tries ComfyUI format first ("prompt" chunk), then A1111/Forge ("parameters" chunk).
    /// </summary>
    public ImageGenerationData Parse(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var fileName = Path.GetFileName(filePath);

        try
        {
            var (width, height) = ReadPngDimensions(filePath);

            var chunks = PngChunkReader.ReadTextChunks(filePath);

            // Try ComfyUI format first (JSON workflow graph in "prompt" chunk)
            if (chunks.TryGetValue("prompt", out var promptJson) && !string.IsNullOrWhiteSpace(promptJson))
            {
                var graph = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(promptJson);
                if (graph is not null)
                {
                    return ParseComfyUiGraph(graph, fileName, width, height);
                }
            }

            // Try A1111 / Forge format ("parameters" chunk with plain-text format)
            if (chunks.TryGetValue("parameters", out var parameters) && !string.IsNullOrWhiteSpace(parameters))
            {
                return ParseA1111Parameters(parameters, fileName, width, height);
            }

            return new ImageGenerationData
            {
                FileName = fileName,
                Width = width,
                Height = height,
                HasData = false
            };
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

    private static ImageGenerationData ParseComfyUiGraph(
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

    #region A1111 / Forge parameter parsing

    /// <summary>
    /// Regex for LoRA references in A1111 prompt text: &lt;lora:name:strength&gt;
    /// </summary>
    [GeneratedRegex(@"<lora:([^:>]+):([^>]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex LoraTagRegex();

    /// <summary>
    /// Parses the plain-text "parameters" format used by Automatic1111 and Forge UI.
    /// Format:
    /// <code>
    /// positive prompt text
    /// Negative prompt: negative prompt text
    /// Steps: 28, Sampler: Euler a, Schedule type: Automatic, CFG scale: 7, Seed: 12345, Size: 512x768, Model: name, ...
    /// </code>
    /// </summary>
    private static ImageGenerationData ParseA1111Parameters(
        string parameters,
        string fileName,
        int width,
        int height)
    {
        // Split into the three logical sections.
        // The last line contains key-value settings.
        // "Negative prompt:" separates positive from negative.
        string? positivePrompt = null;
        string? negativePrompt = null;

        // Find the last line (settings line) — it always starts with "Steps:"
        var lastNewline = parameters.LastIndexOf('\n');
        string settingsLine;
        string promptBlock;

        if (lastNewline >= 0 && parameters.AsSpan(lastNewline + 1).TrimStart().StartsWith("Steps:", StringComparison.OrdinalIgnoreCase))
        {
            settingsLine = parameters[(lastNewline + 1)..].Trim();
            promptBlock = parameters[..lastNewline].Trim();
        }
        else
        {
            // Some images have no settings line — try to find "Steps:" anywhere
            var stepsIdx = parameters.IndexOf("Steps:", StringComparison.OrdinalIgnoreCase);
            if (stepsIdx >= 0)
            {
                // Walk back to the start of that line
                var lineStart = parameters.LastIndexOf('\n', stepsIdx);
                settingsLine = parameters[(lineStart >= 0 ? lineStart + 1 : stepsIdx)..].Trim();
                promptBlock = lineStart >= 0 ? parameters[..lineStart].Trim() : "";
            }
            else
            {
                settingsLine = "";
                promptBlock = parameters.Trim();
            }
        }

        // Split prompt block into positive / negative
        var negIdx = promptBlock.IndexOf("Negative prompt:", StringComparison.OrdinalIgnoreCase);
        if (negIdx >= 0)
        {
            positivePrompt = promptBlock[..negIdx].Trim();
            negativePrompt = promptBlock[(negIdx + "Negative prompt:".Length)..].Trim();
        }
        else
        {
            positivePrompt = promptBlock.Length > 0 ? promptBlock : null;
        }

        // Extract LoRA tags from prompts before returning them
        var loras = new List<LoraInfo>();
        if (positivePrompt is not null)
        {
            ExtractLoraTagsFromPrompt(positivePrompt, loras);
        }

        if (negativePrompt is not null)
        {
            ExtractLoraTagsFromPrompt(negativePrompt, loras);
        }

        // Parse settings key-value pairs from the last line
        var settings = ParseA1111SettingsLine(settingsLine);

        settings.TryGetValue("Sampler", out var samplerName);
        settings.TryGetValue("Schedule type", out var scheduler);
        settings.TryGetValue("Model", out var checkpoint);

        int? steps = settings.TryGetValue("Steps", out var stepsStr) && int.TryParse(stepsStr, out var st) ? st : null;
        long? seed = settings.TryGetValue("Seed", out var seedStr) && long.TryParse(seedStr, out var sd) ? sd : null;
        double? cfg = settings.TryGetValue("CFG scale", out var cfgStr) && double.TryParse(cfgStr, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : null;
        double? denoise = settings.TryGetValue("Denoising strength", out var denStr) && double.TryParse(denStr, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

        // Override dimensions from "Size:" if present and we couldn't read IHDR
        if (width == 0 && height == 0 && settings.TryGetValue("Size", out var sizeStr))
        {
            var parts = sizeStr.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            {
                width = w;
                height = h;
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

    /// <summary>
    /// Parses the comma-separated "Key: Value" settings line from A1111/Forge metadata.
    /// Handles quoted values (e.g. Lora hashes) and values containing colons.
    /// </summary>
    private static Dictionary<string, string> ParseA1111SettingsLine(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(line)) return result;

        // Split on ", " but respect quoted values
        var span = line.AsSpan();
        while (span.Length > 0)
        {
            // Find the key-value separator ": "
            var colonIdx = span.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx < 0) break;

            var key = span[..colonIdx].Trim().ToString();
            span = span[(colonIdx + 2)..];

            // Find the end of the value — next ", <Key>:" pattern or end of string
            string value;
            if (span.Length > 0 && span[0] == '"')
            {
                // Quoted value — find closing quote
                var closeQuote = span[1..].IndexOf('"');
                if (closeQuote >= 0)
                {
                    value = span[1..(closeQuote + 1)].ToString();
                    span = closeQuote + 2 < span.Length ? span[(closeQuote + 2)..] : [];
                    // Skip ", " after closing quote
                    if (span.StartsWith(", ", StringComparison.Ordinal))
                        span = span[2..];
                }
                else
                {
                    value = span.ToString();
                    span = [];
                }
            }
            else
            {
                // Unquoted — find next ", " followed by a key (word with ": ")
                var nextSep = FindNextSettingSeparator(span);
                if (nextSep >= 0)
                {
                    value = span[..nextSep].Trim().ToString();
                    span = span[(nextSep + 2)..]; // skip ", "
                }
                else
                {
                    value = span.Trim().ToString();
                    span = [];
                }
            }

            result.TryAdd(key, value);
        }

        return result;
    }

    /// <summary>
    /// Finds the next ", " separator that is followed by a known settings key pattern (word + ": ").
    /// Returns the index of the comma, or -1 if not found.
    /// </summary>
    private static int FindNextSettingSeparator(ReadOnlySpan<char> span)
    {
        var searchStart = 0;
        while (searchStart < span.Length)
        {
            var idx = span[searchStart..].IndexOf(", ", StringComparison.Ordinal);
            if (idx < 0) return -1;

            var absoluteIdx = searchStart + idx;
            var after = span[(absoluteIdx + 2)..];

            // Check if what follows looks like "Key: " (letters/spaces followed by ": ")
            var nextColon = after.IndexOf(": ", StringComparison.Ordinal);
            if (nextColon > 0)
            {
                // Verify the key part contains only valid key characters
                var keyCandidate = after[..nextColon];
                var isValidKey = true;
                foreach (var ch in keyCandidate)
                {
                    if (!char.IsLetterOrDigit(ch) && ch != ' ' && ch != '_')
                    {
                        isValidKey = false;
                        break;
                    }
                }

                if (isValidKey)
                    return absoluteIdx;
            }

            searchStart = absoluteIdx + 2;
        }

        return -1;
    }

    /// <summary>
    /// Extracts &lt;lora:name:strength&gt; tags from A1111-style prompt text.
    /// </summary>
    private static void ExtractLoraTagsFromPrompt(string prompt, List<LoraInfo> loras)
    {
        foreach (var match in LoraTagRegex().EnumerateMatches(prompt))
        {
            var fullMatch = prompt.AsSpan(match.Index, match.Length);
            // Parse via the Regex object to get groups
            var regexMatch = LoraTagRegex().Match(prompt, match.Index, match.Length);
            if (!regexMatch.Success) continue;

            var name = regexMatch.Groups[1].Value;
            var strengthStr = regexMatch.Groups[2].Value;

            if (string.IsNullOrEmpty(name)) continue;

            double strength = 1.0;
            if (double.TryParse(strengthStr, System.Globalization.CultureInfo.InvariantCulture, out var s))
                strength = s;

            // Avoid duplicates from positive+negative
            if (!loras.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                loras.Add(new LoraInfo
                {
                    Name = name,
                    StrengthModel = strength,
                    StrengthClip = strength
                });
            }
        }
    }

    #endregion

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
    [Obsolete("Use ImageHeaderReader.ReadDimensions for multi-format header-only dimension reading.")]
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
