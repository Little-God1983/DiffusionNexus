using System.Text.Json;
using System.Text.RegularExpressions;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Parses image generation metadata embedded in PNG text chunks.
/// Supports ComfyUI (JSON workflow graph in "prompt" chunk) and
/// Automatic1111 / Forge UI (plain-text "parameters" chunk).
/// </summary>
internal sealed partial class ImageMetadataParser
{
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
            var (width, height) = new ImageHeaderReader().ReadDimensions(filePath);

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
        => DiffusionNexus.UI.Services.Distiller.ComfyUiPromptTracer.Trace(graph, fileName, width, height);

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

        // Extract LoRA tags into the list AND strip them from the stored prompt text, so a
        // re-formatted A1111/AI2Go image does not double-append LoRA tokens (matches the ComfyUI
        // trace path, whose prompts are already token-free).
        var loras = new List<LoraInfo>();
        if (positivePrompt is not null)
        {
            positivePrompt = ExtractLoraTagsFromPrompt(positivePrompt, loras);
        }

        if (negativePrompt is not null)
        {
            negativePrompt = ExtractLoraTagsFromPrompt(negativePrompt, loras);
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
    private static string ExtractLoraTagsFromPrompt(string prompt, List<LoraInfo> loras)
    {
        foreach (var match in LoraTagRegex().EnumerateMatches(prompt))
        {
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

        // Remove the tokens from the prompt text and tidy the separators/whitespace left behind.
        var cleaned = LoraTagRegex().Replace(prompt, "");
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"\s*,\s*", ", ");
        cleaned = Regex.Replace(cleaned, @"(,\s*){2,}", ", ");
        return cleaned.Trim().Trim(',').Trim();
    }

    #endregion
}
