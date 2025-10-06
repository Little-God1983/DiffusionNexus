using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiffusionNexus.UI.Classes;

public static class LoraVariantClassifier
{
    public const string DefaultVariantLabel = "Default";

    private static readonly Dictionary<string, string> VariantLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["highnoise"] = "High",
        ["high"] = "High",
        ["h"] = "High",
        ["lownoise"] = "Low",
        ["low"] = "Low",
        ["l"] = "Low",
    };

    private static readonly string[] VariantKeysByLength = VariantLabels
        .Keys
        .OrderByDescending(k => k.Length)
        .ToArray();

    private static readonly char[] TokenSeparators =
    {
        ' ', '_', '-', '.', '(', ')', '[', ']', '{', '}',
    };

    public static LoraVariantClassification Classify(ModelClass model)
    {
        if (model == null)
        {
            return new LoraVariantClassification(string.Empty, DefaultVariantLabel);
        }

        return Classify(model.SafeTensorFileName);
    }

    public static LoraVariantClassification Classify(string? safeTensorName)
    {
        var baseName = Path.GetFileNameWithoutExtension(safeTensorName ?? string.Empty) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return new LoraVariantClassification(string.Empty, DefaultVariantLabel);
        }

        var tokens = baseName
            .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (tokens.Count == 0)
        {
            return new LoraVariantClassification(NormalizeKey(baseName), DefaultVariantLabel);
        }

        string variantLabel = DefaultVariantLabel;
        if (TryExtractVariant(tokens, out var label))
        {
            variantLabel = label;
        }
        else if (TryExtractVariantFromCombined(baseName, out var combinedLabel, out var combinedKey))
        {
            variantLabel = combinedLabel;
            if (!string.IsNullOrWhiteSpace(combinedKey))
            {
                return new LoraVariantClassification(combinedKey, variantLabel);
            }
        }

        var normalizedKey = NormalizeKey(tokens);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            normalizedKey = NormalizeKey(baseName);
        }

        return new LoraVariantClassification(normalizedKey, variantLabel);
    }

    private static bool TryExtractVariant(List<string> tokens, out string variantLabel)
    {
        for (int length = Math.Min(3, tokens.Count); length >= 1; length--)
        {
            for (int start = tokens.Count - length; start >= 0; start--)
            {
                var candidateTokens = tokens.Skip(start).Take(length);
                var normalized = NormalizeKey(candidateTokens);
                if (TryGetVariantLabel(normalized, out variantLabel!))
                {
                    tokens.RemoveRange(start, length);
                    return true;
                }
            }
        }

        variantLabel = DefaultVariantLabel;
        return false;
    }

    private static bool TryExtractVariantFromCombined(
        string baseName,
        out string variantLabel,
        out string normalizedKey)
    {
        var normalized = NormalizeKey(baseName);
        foreach (var variantKey in VariantKeysByLength)
        {
            if (variantKey.IndexOf("noise", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var index = normalized.LastIndexOf(variantKey, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            if (!TryGetVariantLabel(variantKey, out variantLabel!))
            {
                continue;
            }

            var trimmed = normalized.Remove(index, variantKey.Length);
            normalizedKey = string.IsNullOrWhiteSpace(trimmed) ? normalized : trimmed;
            return true;
        }

        variantLabel = DefaultVariantLabel;
        normalizedKey = normalized;
        return false;
    }

    private static bool TryGetVariantLabel(string candidate, out string variantLabel)
    {
        if (VariantLabels.TryGetValue(candidate, out variantLabel!))
        {
            return true;
        }

        var trimmed = candidate;
        while (trimmed.Length > 0 && char.IsDigit(trimmed[^1]))
        {
            trimmed = trimmed[..^1];
        }

        if (trimmed.Length != candidate.Length && trimmed.Length > 0)
        {
            return VariantLabels.TryGetValue(trimmed, out variantLabel!);
        }

        variantLabel = DefaultVariantLabel;
        return false;
    }

    private static string NormalizeKey(IEnumerable<string> tokens)
    {
        var joined = string.Join('_', tokens);
        return NormalizeKey(joined);
    }

    private static string NormalizeKey(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var chars = text.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }
}

public readonly record struct LoraVariantClassification(string NormalizedKey, string VariantLabel);
