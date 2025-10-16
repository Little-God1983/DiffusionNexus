using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DiffusionNexus.UI.ViewModels;

internal sealed record LoraVariantClassification(string NormalizedKey, string? VariantLabel);

internal static class LoraVariantClassifier
{
    private static readonly Dictionary<string, string> VariantLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["highnoise"] = "High",
        ["high_noise"] = "High",
        ["high"] = "High",
        ["h"] = "High",
        ["hn"] = "High",
        ["lownoise"] = "Low",
        ["low_noise"] = "Low",
        ["low"] = "Low",
        ["l"] = "Low",
        ["ln"] = "Low",
    };

    private static readonly char[] TokenSeparators =
    {
        ' ', '_', '-', '.', '(', ')', '[', ']', '{', '}',
    };

    public static LoraVariantClassification Classify(ModelClass model)
    {
        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        var primary = NormalizeSource(model.SafeTensorFileName);
        var classification = ClassifyFromSource(primary);

        if (string.IsNullOrWhiteSpace(classification.VariantLabel) || string.IsNullOrWhiteSpace(classification.NormalizedKey))
        {
            var fallback = NormalizeSource(model.ModelVersionName);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                var fallbackResult = ClassifyFromSource(fallback);
                var variant = !string.IsNullOrWhiteSpace(classification.VariantLabel)
                    ? classification.VariantLabel
                    : fallbackResult.VariantLabel;

                var key = !string.IsNullOrWhiteSpace(classification.NormalizedKey)
                    ? classification.NormalizedKey
                    : fallbackResult.NormalizedKey;

                classification = new LoraVariantClassification(key, variant);
            }
        }

        return classification;
    }

    private static string? NormalizeSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var extension = Path.GetExtension(trimmed);

        if (!string.IsNullOrWhiteSpace(extension) && IsKnownExtension(extension))
        {
            var withoutExtension = Path.GetFileNameWithoutExtension(trimmed);
            return string.IsNullOrWhiteSpace(withoutExtension) ? trimmed : withoutExtension;
        }

        return trimmed;
    }

    private static bool IsKnownExtension(string extension) => extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pt", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ckpt", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);

    private static LoraVariantClassification ClassifyFromSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new LoraVariantClassification(string.Empty, null);
        }

        var label = DetectVariantLabel(source);
        var key = BuildNormalizedKey(source, label);
        return new LoraVariantClassification(key, label);
    }

    private static string? DetectVariantLabel(string source)
    {
        foreach (var kvp in VariantLabels.OrderByDescending(k => k.Key.Length))
        {
            if (ContainsToken(source, kvp.Key))
            {
                return kvp.Value;
            }
        }

        var tokens = Tokenize(source);
        foreach (var token in tokens)
        {
            if (VariantLabels.TryGetValue(token, out var label))
            {
                return label;
            }

            var trimmed = TrimNumericEdges(token);
            if (!string.Equals(trimmed, token, StringComparison.OrdinalIgnoreCase) &&
                VariantLabels.TryGetValue(trimmed, out label))
            {
                return label;
            }

            label = DetectEmbeddedVariant(token);
            if (!string.IsNullOrWhiteSpace(label))
            {
                return label;
            }
        }

        return null;
    }

    private static string? DetectEmbeddedVariant(string token)
    {
        foreach (var kvp in VariantLabels.OrderByDescending(k => k.Key.Length))
        {
            var key = kvp.Key;
            if (key.Length == 1)
            {
                continue;
            }

            var index = token.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var before = index > 0 ? token[index - 1] : (char?)null;
                var afterIndex = index + key.Length;
                var after = afterIndex < token.Length ? token[afterIndex] : (char?)null;

                var beforeIsLetter = IsAlphabetic(before);
                var afterIsLetter = IsAlphabetic(after);

                if (!beforeIsLetter || !afterIsLetter)
                {
                    return kvp.Value;
                }

                if (key.Length > 3 && before.HasValue && after.HasValue &&
                    char.IsUpper(before.Value) && char.IsUpper(after.Value))
                {
                    return kvp.Value;
                }

                index = token.IndexOf(key, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        return null;
    }

    private static bool IsAlphabetic(char? value) => value.HasValue && char.IsLetter(value.Value);

    private static string BuildNormalizedKey(string source, string? variantLabel)
    {
        var sanitized = RemoveVariantSegments(source);
        var tokens = Tokenize(sanitized);

        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var processed = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var lower = token.ToLower(CultureInfo.InvariantCulture);

            if (VariantLabels.ContainsKey(lower))
            {
                continue;
            }

            if (string.Equals(lower, "noise", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsVersionToken(lower))
            {
                continue;
            }

            if (StartsWithDigit(lower))
            {
                if (lower.All(char.IsDigit))
                {
                    if (!HasSubsequentAlphabeticToken(tokens, i))
                    {
                        continue;
                    }
                }
                else
                {
                    if (HasOtherAlphabeticToken(tokens, i))
                    {
                        continue;
                    }
                }
            }

            var normalized = NormalizeToken(token, variantLabel);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                processed.Add(normalized);
            }
        }

        return processed.Count == 0
            ? string.Empty
            : string.Concat(processed).ToLower(CultureInfo.InvariantCulture);
    }

    private static List<string> Tokenize(string source)
    {
        return source
            .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
    }

    private static string RemoveVariantSegments(string source)
    {
        var result = source;
        foreach (var key in VariantLabels.Keys.OrderByDescending(k => k.Length))
        {
            result = RemoveToken(result, key);
        }

        return result;
    }

    private static string RemoveToken(string source, string token)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(token))
        {
            return source;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while (index < source.Length)
        {
            var found = source.IndexOf(token, index, comparison);
            if (found < 0)
            {
                break;
            }

            var startBoundary = found == 0 || !char.IsLetterOrDigit(source[found - 1]);
            var endPos = found + token.Length;
            var endBoundary = endPos >= source.Length || !char.IsLetterOrDigit(source[endPos]);

            if (startBoundary && endBoundary)
            {
                source = source.Remove(found, token.Length);
                continue;
            }

            index = found + 1;
        }

        return source;
    }

    private static bool ContainsToken(string source, string token)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while (index < source.Length)
        {
            var found = source.IndexOf(token, index, comparison);
            if (found < 0)
            {
                return false;
            }

            var startBoundary = found == 0 || !char.IsLetterOrDigit(source[found - 1]);
            var endPos = found + token.Length;
            var endBoundary = endPos >= source.Length || !char.IsLetterOrDigit(source[endPos]);

            if (startBoundary && endBoundary)
            {
                return true;
            }

            index = found + 1;
        }

        return false;
    }

    private static string NormalizeToken(string token, string? variantLabel)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        if (variantLabel is null)
        {
            return token;
        }

        if (string.Equals(variantLabel, "High", StringComparison.OrdinalIgnoreCase))
        {
            token = TrimSuffix(token, "hn");
            token = TrimSuffix(token, "h");
        }
        else if (string.Equals(variantLabel, "Low", StringComparison.OrdinalIgnoreCase))
        {
            token = TrimSuffix(token, "ln");
            token = TrimSuffix(token, "l");
        }

        foreach (var key in GetVariantKeys(variantLabel))
        {
            token = RemoveSubstring(token, key);
        }

        return token;
    }

    private static IEnumerable<string> GetVariantKeys(string? variantLabel)
    {
        if (string.IsNullOrWhiteSpace(variantLabel))
        {
            yield break;
        }

        foreach (var pair in VariantLabels)
        {
            if (string.Equals(pair.Value, variantLabel, StringComparison.OrdinalIgnoreCase))
            {
                if (pair.Key.Length == 1)
                {
                    continue;
                }

                yield return pair.Key;
            }
        }
    }

    private static string RemoveSubstring(string token, string key)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(key))
        {
            return token;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var index = token.IndexOf(key, comparison);
        while (index >= 0)
        {
            var before = index > 0 ? token[index - 1] : (char?)null;
            var afterIndex = index + key.Length;
            var after = afterIndex < token.Length ? token[afterIndex] : (char?)null;

            var beforeIsLetter = IsAlphabetic(before);
            var afterIsLetter = IsAlphabetic(after);

            var shouldRemove = !beforeIsLetter || !afterIsLetter;

            if (!shouldRemove && before.HasValue && after.HasValue &&
                key.Length > 3 && char.IsUpper(before.Value) && char.IsUpper(after.Value))
            {
                shouldRemove = true;
            }

            if (!shouldRemove)
            {
                index = token.IndexOf(key, index + 1, comparison);
                continue;
            }

            token = token.Remove(index, key.Length);
            index = token.IndexOf(key, comparison);
        }

        return token;
    }

    private static string TrimSuffix(string token, string suffix)
    {
        if (token.Length <= suffix.Length)
        {
            return token;
        }

        if (token.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            if (token.Length - suffix.Length < 4)
            {
                return token;
            }

            var segment = token[^suffix.Length..];
            if (!segment.Any(char.IsUpper))
            {
                return token;
            }

            var preceding = token[token.Length - suffix.Length - 1];
            if (!char.IsDigit(preceding))
            {
                return token[..^suffix.Length];
            }
        }

        return token;
    }

    private static string TrimNumericEdges(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        var start = 0;
        while (start < token.Length && char.IsDigit(token[start]))
        {
            start++;
        }

        var end = token.Length - 1;
        while (end >= start && char.IsDigit(token[end]))
        {
            end--;
        }

        return start > end ? string.Empty : token[start..(end + 1)];
    }

    private static bool IsVersionToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token.StartsWith("ver", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("version", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(token, "v", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (token[0] == 'v' && token.Length > 1 && token[1..].All(char.IsDigit))
        {
            return true;
        }

        if (token[0] == 'e' && token.Length > 1 && token[1..].All(char.IsDigit))
        {
            return true;
        }

        if (token.StartsWith("epoch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool StartsWithDigit(string token) => !string.IsNullOrWhiteSpace(token) && char.IsDigit(token[0]);

    private static bool HasOtherAlphabeticToken(IReadOnlyList<string> tokens, int excludeIndex)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i == excludeIndex)
            {
                continue;
            }

            var token = tokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var lower = token.ToLower(CultureInfo.InvariantCulture);

            if (VariantLabels.ContainsKey(lower))
            {
                continue;
            }

            if (IsVersionToken(lower))
            {
                continue;
            }

            if (lower.Any(char.IsLetter))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSubsequentAlphabeticToken(IReadOnlyList<string> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var lower = token.ToLower(CultureInfo.InvariantCulture);
            if (VariantLabels.ContainsKey(lower))
            {
                continue;
            }

            if (IsVersionToken(lower))
            {
                continue;
            }

            if (lower.All(char.IsDigit))
            {
                continue;
            }

            if (lower.Any(char.IsLetter))
            {
                return true;
            }
        }

        return false;
    }
}
