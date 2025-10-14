using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents the normalized classification data extracted from a LoRA model name.
/// </summary>
/// <param name="NormalizedKey">Deterministic key used to group different variants of the same LoRA.</param>
/// <param name="VariantLabel">Human friendly label describing the detected variant (High, Low, etc.).</param>
internal sealed record LoraVariantClassification(string NormalizedKey, string? VariantLabel);

/// <summary>
/// Provides functionality for parsing a <see cref="ModelClass"/> into a <see cref="LoraVariantClassification"/>.
/// </summary>
internal interface ILoraNameParser
{
    /// <summary>
    /// Produces a <see cref="LoraVariantClassification"/> for the supplied model.
    /// </summary>
    /// <param name="model">The model metadata extracted from Civitai.</param>
    /// <returns>A normalized classification for the model.</returns>
    LoraVariantClassification Classify(ModelClass model);
}

/// <summary>
/// Entry point used by the UI to classify LoRA models and derive variant groupings.
/// </summary>
internal static class LoraVariantClassifier
{
    private static readonly ILoraNameParser Parser = new DefaultLoraNameParser();

    /// <summary>
    /// Classifies the supplied model and returns the normalized grouping key and variant label.
    /// </summary>
    /// <param name="model">The model metadata to inspect.</param>
    /// <returns>A <see cref="LoraVariantClassification"/> describing the detected variant.</returns>
    public static LoraVariantClassification Classify(ModelClass model) => Parser.Classify(model);

    /// <summary>
    /// The default parser implementation used by the application. Exposed for testing.
    /// </summary>
    internal static ILoraNameParser DefaultParser => Parser;

    /// <summary>
    /// Default implementation that performs name parsing and normalization using heuristics.
    /// </summary>
    private sealed class DefaultLoraNameParser : ILoraNameParser
    {
        private static readonly string[] KnownExtensions =
        {
            ".safetensors",
            ".pt",
            ".ckpt",
            ".bin"
        };

        private static readonly char[] TokenSeparators =
        {
            ' ', '_', '-', '.', '(', ')', '[', ']', '{', '}'
        };

        private static readonly VariantTokenCatalog VariantTokens = VariantTokenCatalog.CreateDefault();

        /// <inheritdoc />
        public LoraVariantClassification Classify(ModelClass model)
        {
            if (model is null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            LoraVariantClassification primary = ClassifyFromSource(NormalizeSource(model.SafeTensorFileName));
            if (HasMeaningfulResult(primary))
            {
                return primary;
            }

            LoraVariantClassification fallback = ClassifyFromSource(NormalizeSource(model.ModelVersionName));
            return MergeResults(primary, fallback);
        }

        /// <summary>
        /// Combines two partial classification results, preferring non-empty values from the primary source.
        /// </summary>
        private static LoraVariantClassification MergeResults(
            LoraVariantClassification primary,
            LoraVariantClassification fallback)
        {
            string normalizedKey = !string.IsNullOrWhiteSpace(primary.NormalizedKey)
                ? primary.NormalizedKey
                : fallback.NormalizedKey;

            string? variantLabel = !string.IsNullOrWhiteSpace(primary.VariantLabel)
                ? primary.VariantLabel
                : fallback.VariantLabel;

            return new LoraVariantClassification(normalizedKey, variantLabel);
        }

        /// <summary>
        /// Determines whether the classification contains both a normalized key and a variant label.
        /// </summary>
        private static bool HasMeaningfulResult(LoraVariantClassification classification)
        {
            return !string.IsNullOrWhiteSpace(classification.NormalizedKey)
                && !string.IsNullOrWhiteSpace(classification.VariantLabel);
        }

        /// <summary>
        /// Attempts to classify a single textual source (file name or model version).
        /// </summary>
        private static LoraVariantClassification ClassifyFromSource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return new LoraVariantClassification(string.Empty, null);
            }

            string? variantLabel = DetectVariantLabel(source);
            string normalizedKey = BuildNormalizedKey(source, variantLabel);
            return new LoraVariantClassification(normalizedKey, variantLabel);
        }

        /// <summary>
        /// Normalizes the model source name by trimming whitespace and removing known file extensions.
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
        /// Checks whether the supplied extension should be stripped when normalizing sources.
        /// </summary>
        private static bool IsKnownExtension(string extension)
        {
            foreach (string candidate in KnownExtensions)
            {
                if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines the variant label (High/Low) by scanning the source for known tokens.
        /// </summary>
        private static string? DetectVariantLabel(string source)
        {
            if (TryDetectStandaloneVariant(source, out string? label))
            {
                return label;
            }

            List<string> tokens = Tokenize(source);
            foreach (string token in tokens)
            {
                if (TryResolveVariantToken(token, out label))
                {
                    return label;
                }

                if (TryTrimNumericEdges(token, out string trimmed) && TryResolveVariantToken(trimmed, out label))
                {
                    return label;
                }

                if (TryDetectEmbeddedVariant(token, out label))
                {
                    return label;
                }
            }

            return null;
        }

        /// <summary>
        /// Detects variant labels that appear as standalone tokens inside the source string.
        /// </summary>
        private static bool TryDetectStandaloneVariant(string source, out string? label)
        {
            foreach (string token in VariantTokens.TokensByLength)
            {
                if (ContainsStandaloneToken(source, token))
                {
                    label = VariantTokens[token];
                    return true;
                }
            }

            label = null;
            return false;
        }

        /// <summary>
        /// Tries to resolve a single token against the known variant catalog.
        /// </summary>
        private static bool TryResolveVariantToken(string token, out string? label)
        {
            if (VariantTokens.TryGetLabel(token, out string? value))
            {
                label = value;
                return true;
            }

            label = null;
            return false;
        }

        /// <summary>
        /// Attempts to detect variant tokens embedded in longer identifiers (e.g. WAN2.2HighNoise).
        /// </summary>
        private static bool TryDetectEmbeddedVariant(string token, out string? label)
        {
            foreach (string variantToken in VariantTokens.TokensByLength)
            {
                int index = token.IndexOf(variantToken, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    char? before = index > 0 ? token[index - 1] : null;
                    int afterIndex = index + variantToken.Length;
                    char? after = afterIndex < token.Length ? token[afterIndex] : null;

                    if (!IsLowercaseLetter(before) && !IsLowercaseLetter(after))
                    {
                        label = VariantTokens[variantToken];
                        return true;
                    }

                    index = token.IndexOf(variantToken, index + 1, StringComparison.OrdinalIgnoreCase);
                }
            }

            label = null;
            return false;
        }

        /// <summary>
        /// Determines whether the supplied character is a lowercase letter.
        /// </summary>
        private static bool IsLowercaseLetter(char? value) => value.HasValue && char.IsLetter(value.Value) && char.IsLower(value.Value);

        /// <summary>
        /// Builds the normalized grouping key by stripping variant specific segments and noise descriptors.
        /// </summary>
        private static string BuildNormalizedKey(string source, string? variantLabel)
        {
            string sanitized = RemoveVariantSegments(source);
            List<string> tokens = Tokenize(sanitized);
            if (tokens.Count == 0)
            {
                return string.Empty;
            }

            var processed = new List<string>();
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (ShouldSkipToken(tokens, i, token))
                {
                    continue;
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
        /// Determines whether the token should be ignored when building the normalized key.
        /// </summary>
        private static bool ShouldSkipToken(IReadOnlyList<string> tokens, int index, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            string lower = token.ToLower(CultureInfo.InvariantCulture);

            if (VariantTokens.Contains(lower))
            {
                return true;
            }

            if (string.Equals(lower, "noise", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IsVersionToken(lower))
            {
                return true;
            }

            if (StartsWithDigit(lower))
            {
                if (lower.All(char.IsDigit))
                {
                    return !HasSubsequentAlphabeticToken(tokens, index);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Splits the source string into trimmed tokens.
        /// </summary>
        private static List<string> Tokenize(string source)
        {
            return source
                .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim())
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToList();
        }

        /// <summary>
        /// Removes known variant descriptors from the original source string.
        /// </summary>
        private static string RemoveVariantSegments(string source)
        {
            string result = source;
            foreach (string token in VariantTokens.TokensByLength)
            {
                result = RemoveToken(result, token);
            }

            return result;
        }

        /// <summary>
        /// Removes a standalone token from the source while respecting alphanumeric boundaries.
        /// </summary>
        private static string RemoveToken(string source, string token)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(token))
            {
                return source;
            }

            int index = 0;
            while (index < source.Length)
            {
                int found = source.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
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
        /// Determines whether the source contains a standalone token separated by non alphanumeric characters.
        /// </summary>
        private static bool ContainsStandaloneToken(string source, string token)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            int index = 0;
            while (index < source.Length)
            {
                int found = source.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
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
        /// Removes variant specific suffixes and substrings from the token.
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

            foreach (string key in VariantTokens.GetTokensForLabel(variantLabel))
            {
                token = RemoveSubstring(token, key);
            }

            return token;
        }

        /// <summary>
        /// Enumerates the variant tokens that map to the specified label.
        /// </summary>
        private static string RemoveSubstring(string token, string key)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(key))
            {
                return token;
            }

            int index = token.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                token = token.Remove(index, key.Length);
                index = token.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            }

            return token;
        }

        /// <summary>
        /// Removes the provided suffix when it represents a variant marker appended to the token.
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
        /// Trims numeric characters from the start and end of the token.
        /// </summary>
        private static bool TryTrimNumericEdges(string token, out string trimmed)
        {
            trimmed = token;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
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

            if (start == 0 && end == token.Length - 1)
            {
                return false;
            }

            trimmed = start > end ? string.Empty : token[start..(end + 1)];
            return true;
        }

        /// <summary>
        /// Determines whether the supplied token refers to a version identifier (v1, epoch10, etc.).
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
        /// Determines whether any of the subsequent tokens contain alphabetic characters.
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
                if (VariantTokens.Contains(lower))
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

    /// <summary>
    /// Catalog of variant tokens (High/Low aliases) used for detection and normalization.
    /// </summary>
    private sealed class VariantTokenCatalog
    {
        private readonly Dictionary<string, string> _tokens;
        private readonly Dictionary<string, IReadOnlyList<string>> _tokensByLabel;
        private readonly IReadOnlyList<string> _tokensByLength;

        private VariantTokenCatalog(Dictionary<string, string> tokens)
        {
            _tokens = tokens;
            _tokensByLength = tokens.Keys
                .OrderByDescending(key => key.Length)
                .ToList();

            _tokensByLabel = tokens
                .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Select(pair => pair.Key).ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the tokens sorted by descending length. Used to prefer longer matches first.
        /// </summary>
        public IReadOnlyList<string> TokensByLength => _tokensByLength;

        /// <summary>
        /// Retrieves the label for a token. Throws if the token is unknown.
        /// </summary>
        public string this[string token] => _tokens[token];

        /// <summary>
        /// Checks if the catalog contains the specified token (case insensitive).
        /// </summary>
        public bool Contains(string token) => _tokens.ContainsKey(token);

        /// <summary>
        /// Attempts to resolve the label associated with a token.
        /// </summary>
        public bool TryGetLabel(string token, out string? label)
        {
            return _tokens.TryGetValue(token, out label);
        }

        /// <summary>
        /// Enumerates the known tokens that produce the supplied label.
        /// </summary>
        public IEnumerable<string> GetTokensForLabel(string label)
        {
            if (_tokensByLabel.TryGetValue(label, out IReadOnlyList<string>? tokens))
            {
                foreach (string token in tokens)
                {
                    yield return token;
                }
            }
        }

        /// <summary>
        /// Creates the default catalog with the WAN naming aliases.
        /// </summary>
        public static VariantTokenCatalog CreateDefault()
        {
            var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["highnoise"] = "High",
                ["high_noise"] = "High",
                ["_high_noise"] = "High",
                ["h_high_noise"] = "High",
                ["high"] = "High",
                ["_high_"] = "High",
                ["hn"] = "High",
                ["lownoise"] = "Low",
                ["low_noise"] = "Low",
                ["_low_noise"] = "Low",
                ["l_low_noise"] = "Low",
                ["low"] = "Low",
                ["_low_"] = "High",
                ["ln"] = "Low"
            };

            return new VariantTokenCatalog(tokens);
        }
    }
}
