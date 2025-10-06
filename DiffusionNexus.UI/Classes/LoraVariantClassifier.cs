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
        ["high"] = "High",
        ["h"] = "High",
        ["highnoise"] = "High Noise",
        ["lownoise"] = "Low Noise",
        ["low"] = "Low",
        ["l"] = "Low",
    };

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

        var normalizedKey = NormalizeKey(tokens);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            normalizedKey = NormalizeKey(baseName);
        }

        return new LoraVariantClassification(normalizedKey, variantLabel);
    }

    private static bool TryExtractVariant(List<string> tokens, out string variantLabel)
    {
        for (int length = Math.Min(2, tokens.Count); length >= 1; length--)
        {
            var candidateTokens = tokens.Skip(tokens.Count - length).Take(length);
            var normalized = NormalizeKey(candidateTokens);
            if (VariantLabels.TryGetValue(normalized, out variantLabel!))
            {
                tokens.RemoveRange(tokens.Count - length, length);
                return true;
            }
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
