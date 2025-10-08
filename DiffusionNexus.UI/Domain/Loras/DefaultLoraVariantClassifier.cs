using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DiffusionNexus.UI.Domain.Loras;

/// <summary>
/// Default implementation of <see cref="ILoraVariantClassifier"/> that encapsulates the legacy naming rules
/// used throughout the UI when grouping LoRA variants.
/// </summary>
internal sealed class DefaultLoraVariantClassifier : ILoraVariantClassifier
{
    private static readonly Dictionary<string, string> VariantLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["highnoise"] = "High",
        ["high_noise"] = "High",
        ["high"] = "High",
        ["hn"] = "High",
        ["lownoise"] = "Low",
        ["low_noise"] = "Low",
        ["low"] = "Low",
        ["ln"] = "Low",
    };

    private static readonly IReadOnlyList<KeyValuePair<string, string>> VariantLabelsByLength = VariantLabels
        .OrderByDescending(pair => pair.Key.Length)
        .ToList();

    private static readonly char[] TokenSeparators =
    {
        ' ', '_', '-', '.', '(', ')', '[', ']', '{', '}',
    };

    /// <inheritdoc />
    public LoraVariantClassification Classify(ModelClass model)
    {
        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        string? primarySource = NormalizeSource(model.SafeTensorFileName);
        LoraVariantClassification classification = ClassifySource(primarySource);

        if (RequiresFallback(classification))
        {
            string? fallbackSource = NormalizeSource(model.ModelVersionName);
            LoraVariantClassification fallback = ClassifySource(fallbackSource);
            classification = MergeClassifications(classification, fallback);
        }

        return classification;
    }

    /// <summary>
    /// Normalizes the provided raw value into a comparable source string.
    /// </summary>
    private static string? NormalizeSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        string extension = Path.GetExtension(trimmed);

        if (!string.IsNullOrWhiteSpace(extension) && IsKnownExtension(extension))
        {
            string withoutExtension = Path.GetFileNameWithoutExtension(trimmed);
            return string.IsNullOrWhiteSpace(withoutExtension) ? trimmed : withoutExtension;
        }

        return trimmed;
    }

    /// <summary>
    /// Determines whether the supplied file extension should be discarded during normalization.
    /// </summary>
    private static bool IsKnownExtension(string extension) => extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pt", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ckpt", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies a single normalized source string.
    /// </summary>
    private static LoraVariantClassification ClassifySource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new LoraVariantClassification(string.Empty, null);
        }

        IReadOnlyList<string> tokens = Tokenize(source);
        string? label = DetectVariantLabel(source, tokens);
        string key = BuildNormalizedKey(source, label);
        return new LoraVariantClassification(key, label);
    }

    /// <summary>
    /// Tokenizes the given source string using the configured separators.
    /// </summary>
    private static IReadOnlyList<string> Tokenize(string source)
    {
        return source
            .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();
    }

    /// <summary>
    /// Detects a High/Low variant label from the provided source and tokens.
    /// </summary>
    private static string? DetectVariantLabel(string source, IReadOnlyList<string> tokens)
    {
        string? label = DetectVariantFromRawSegments(source);
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        foreach (string token in tokens)
        {
            label = DetectVariantFromToken(token);
            if (!string.IsNullOrWhiteSpace(label))
            {
                return label;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to find a variant label by searching raw substrings (e.g. "_high_").
    /// </summary>
    private static string? DetectVariantFromRawSegments(string source)
    {
        foreach (var pair in VariantLabelsByLength)
        {
            if (ContainsToken(source, pair.Key))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to identify a variant from a single token.
    /// </summary>
    private static string? DetectVariantFromToken(string token)
    {
        if (VariantLabels.TryGetValue(token, out string? label))
        {
            return label;
        }

        string trimmed = TrimNumericEdges(token);
        if (!string.Equals(trimmed, token, StringComparison.OrdinalIgnoreCase) && VariantLabels.TryGetValue(trimmed, out label))
        {
            return label;
        }

        return DetectEmbeddedVariant(trimmed);
    }

    /// <summary>
    /// Scans within a token for an embedded variant string surrounded by non-letter characters.
    /// </summary>
    private static string? DetectEmbeddedVariant(string token)
    {
        foreach (var pair in VariantLabelsByLength)
        {
            string key = pair.Key;
            int index = token.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                char? before = index > 0 ? token[index - 1] : null;
                int afterIndex = index + key.Length;
                char? after = afterIndex < token.Length ? token[afterIndex] : null;

                if (!IsLowercaseLetter(before) && !IsLowercaseLetter(after))
                {
                    return pair.Value;
                }

                index = token.IndexOf(key, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether a character represents a lowercase letter.
    /// </summary>
    private static bool IsLowercaseLetter(char? value) => value.HasValue && char.IsLetter(value.Value) && char.IsLower(value.Value);

    /// <summary>
    /// Builds the normalized key used to group LoRA variants.
    /// </summary>
    private static string BuildNormalizedKey(string source, string? variantLabel)
    {
        string sanitized = RemoveVariantSegments(source);
        IReadOnlyList<string> tokens = Tokenize(sanitized);

        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var processed = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            string lower = token.ToLower(CultureInfo.InvariantCulture);

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
                    continue;
                }
            }

            string normalized = NormalizeToken(token, variantLabel);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                processed.Add(normalized);
            }
        }

        return processed.Count == 0
            ? string.Empty
            : string.Concat(processed).ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Removes the variant segments from the raw source prior to tokenizing.
    /// </summary>
    private static string RemoveVariantSegments(string source)
    {
        string result = source;
        foreach (string key in VariantLabelsByLength.Select(pair => pair.Key))
        {
            result = RemoveToken(result, key);
        }

        return result;
    }

    /// <summary>
    /// Removes an isolated token from the source, respecting token boundaries.
    /// </summary>
    private static string RemoveToken(string source, string token)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(token))
        {
            return source;
        }

        StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        int index = 0;
        while (index < source.Length)
        {
            int found = source.IndexOf(token, index, comparison);
            if (found < 0)
            {
                break;
            }

            bool startBoundary = found == 0 || !char.IsLetterOrDigit(source[found - 1]);
            int endPos = found + token.Length;
            bool endBoundary = endPos >= source.Length || !char.IsLetterOrDigit(source[endPos]);

            if (startBoundary && endBoundary)
            {
                source = source.Remove(found, token.Length);
                continue;
            }

            index = found + 1;
        }

        return source;
    }

    /// <summary>
    /// Determines whether the source contains the specified token, bounded by non-alphanumeric characters.
    /// </summary>
    private static bool ContainsToken(string source, string token)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        int index = 0;
        while (index < source.Length)
        {
            int found = source.IndexOf(token, index, comparison);
            if (found < 0)
            {
                return false;
            }

            bool startBoundary = found == 0 || !char.IsLetterOrDigit(source[found - 1]);
            int endPos = found + token.Length;
            bool endBoundary = endPos >= source.Length || !char.IsLetterOrDigit(source[endPos]);

            if (startBoundary && endBoundary)
            {
                return true;
            }

            index = found + 1;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a token by trimming variant suffixes and removing variant substrings.
    /// </summary>
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

        foreach (string key in GetVariantKeys(variantLabel))
        {
            token = RemoveSubstring(token, key);
        }

        return token;
    }

    /// <summary>
    /// Enumerates all variant keys that map to the specified label.
    /// </summary>
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
                yield return pair.Key;
            }
        }
    }

    /// <summary>
    /// Removes all occurrences of the specified key from the token, ignoring case.
    /// </summary>
    private static string RemoveSubstring(string token, string key)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(key))
        {
            return token;
        }

        StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        int index = token.IndexOf(key, comparison);
        while (index >= 0)
        {
            token = token.Remove(index, key.Length);
            index = token.IndexOf(key, comparison);
        }

        return token;
    }

    /// <summary>
    /// Trims the supplied suffix when it forms an uppercase suffix that is not part of a numeric value.
    /// </summary>
    private static string TrimSuffix(string token, string suffix)
    {
        if (token.Length <= suffix.Length)
        {
            return token;
        }

        if (token.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            string segment = token[^suffix.Length..];
            if (!segment.Any(char.IsUpper))
            {
                return token;
            }

            char preceding = token[token.Length - suffix.Length - 1];
            if (!char.IsDigit(preceding))
            {
                return token[..^suffix.Length];
            }
        }

        return token;
    }

    /// <summary>
    /// Removes leading and trailing digits from a token to assist with variant detection.
    /// </summary>
    private static string TrimNumericEdges(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        int start = 0;
        while (start < token.Length && char.IsDigit(token[start]))
        {
            start++;
        }

        int end = token.Length - 1;
        while (end >= start && char.IsDigit(token[end]))
        {
            end--;
        }

        return start > end ? string.Empty : token[start..(end + 1)];
    }

    /// <summary>
    /// Determines whether the token denotes a version identifier.
    /// </summary>
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

    /// <summary>
    /// Determines whether a token starts with a digit.
    /// </summary>
    private static bool StartsWithDigit(string token) => !string.IsNullOrWhiteSpace(token) && char.IsDigit(token[0]);

    /// <summary>
    /// Determines whether there is a subsequent token containing alphabetic characters.
    /// </summary>
    private static bool HasSubsequentAlphabeticToken(IReadOnlyList<string> tokens, int index)
    {
        for (int i = index + 1; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            string lower = token.ToLower(CultureInfo.InvariantCulture);
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

    /// <summary>
    /// Determines whether the classification requires a fallback value.
    /// </summary>
    private static bool RequiresFallback(LoraVariantClassification classification)
    {
        return string.IsNullOrWhiteSpace(classification.VariantLabel)
            || string.IsNullOrWhiteSpace(classification.NormalizedKey);
    }

    /// <summary>
    /// Combines the primary and fallback classifications according to legacy precedence rules.
    /// </summary>
    private static LoraVariantClassification MergeClassifications(LoraVariantClassification primary, LoraVariantClassification fallback)
    {
        string? variant = !string.IsNullOrWhiteSpace(primary.VariantLabel)
            ? primary.VariantLabel
            : fallback.VariantLabel;

        string key = !string.IsNullOrWhiteSpace(primary.NormalizedKey)
            ? primary.NormalizedKey
            : fallback.NormalizedKey;

        return new LoraVariantClassification(key, variant);
    }
}
