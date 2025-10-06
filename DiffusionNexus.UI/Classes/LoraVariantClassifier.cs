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
        ["hn"] = "High",
        ["lownoise"] = "Low",
        ["low"] = "Low",
        ["l"] = "Low",
        ["ln"] = "Low",
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

        StripEmbeddedVariantTokens(tokens, ref variantLabel);

        if (variantLabel == DefaultVariantLabel && TryExtractVariantFromCombined(baseName, out var combinedLabel, out var combinedKey))
        {
            variantLabel = combinedLabel;
            if (!string.IsNullOrWhiteSpace(combinedKey))
            {
                return new LoraVariantClassification(combinedKey, variantLabel);
            }
        }

        RemoveVersionTokens(tokens);

        var normalizedKey = NormalizeKey(tokens);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            normalizedKey = NormalizeKey(baseName);
        }

        return new LoraVariantClassification(normalizedKey, variantLabel);
    }

    private static void StripEmbeddedVariantTokens(List<string> tokens, ref string variantLabel)
    {
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (TryStripVariantSuffix(token, out var stripped, out var suffixLabel))
            {
                if (string.IsNullOrWhiteSpace(stripped))
                {
                    tokens.RemoveAt(i);
                }
                else
                {
                    tokens[i] = stripped;
                }

                if (variantLabel == DefaultVariantLabel && suffixLabel != DefaultVariantLabel)
                {
                    variantLabel = suffixLabel;
                }
            }
        }
    }

    private static bool TryStripVariantSuffix(string token, out string strippedToken, out string variantLabel)
    {
        strippedToken = token;
        variantLabel = DefaultVariantLabel;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        foreach (var variantKey in VariantKeysByLength)
        {
            if (token.Length <= variantKey.Length)
            {
                continue;
            }

            if (!token.EndsWith(variantKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetVariantLabel(variantKey, out var label))
            {
                continue;
            }

            var suffix = token.Substring(token.Length - variantKey.Length, variantKey.Length);
            if (!suffix.Any(char.IsUpper))
            {
                continue;
            }

            strippedToken = token[..^variantKey.Length];
            variantLabel = label;
            return true;
        }

        return false;
    }

    private static void RemoveVersionTokens(List<string> tokens)
    {
        var removedVersionSuffix = false;

        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            if (!IsVersionToken(tokens, i))
            {
                continue;
            }

            var token = tokens[i];
            if (!removedVersionSuffix && token.Any(char.IsDigit))
            {
                removedVersionSuffix = true;
            }

            tokens.RemoveAt(i);
        }

        if (!removedVersionSuffix)
        {
            return;
        }

        while (tokens.Count > 0 && IsVersionPrefixToken(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
    }

    private static bool IsVersionToken(List<string> tokens, int index)
    {
        var token = tokens[index];
        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        token = token.Trim();
        var lower = token.ToLowerInvariant();

        if (lower.All(char.IsDigit))
        {
            return index >= tokens.Count - 1;
        }

        if (lower.StartsWith('v') && lower.Length > 1 && lower[1..].All(char.IsDigit))
        {
            return true;
        }

        if (lower.StartsWith('e') && lower.Length > 1 && lower[1..].All(char.IsDigit))
        {
            return true;
        }

        if (lower.Contains("epoc") || lower.Contains("epoch") || lower.Contains("iter") || lower.Contains("step"))
        {
            return true;
        }

        if (lower.EndsWith('b') && lower[..^1].All(char.IsDigit))
        {
            return true;
        }

        return false;
    }

    private static bool IsVersionPrefixToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var lower = token.ToLowerInvariant();
        return lower is "v" or "ver" or "vers" or "version";
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
