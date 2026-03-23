using System.Text.RegularExpressions;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.DatasetQuality.Dictionaries;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Text processing utilities shared by all dataset quality checks.
/// All methods are pure functions — no I/O, no state.
/// </summary>
public static class TextHelpers
{
    // Pre-compiled separators
    private static readonly char[] TagSeparators = [','];
    private static readonly char[] WordSeparators = [' ', '\t', '\n', '\r'];

    #region Caption Style Detection

    /// <summary>
    /// Heuristic to detect whether caption text is natural-language prose,
    /// booru-style comma-separated tags, or a mix of both.
    /// </summary>
    /// <remarks>
    /// Signals used:
    /// <list type="bullet">
    /// <item>Comma-to-word ratio (high → tags)</item>
    /// <item>Presence of periods / sentence structure (→ NL)</item>
    /// <item>Presence of articles like "a", "an", "the" (→ NL)</item>
    /// <item>Underscore tokens like "brown_hair" (→ tags)</item>
    /// </list>
    /// </remarks>
    public static CaptionStyle DetectCaptionStyle(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return CaptionStyle.Unknown;

        var trimmed = text.Trim();

        var commaCount = CountChar(trimmed, ',');
        var periodCount = CountChar(trimmed, '.');
        var words = trimmed.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        var wordCount = words.Length;

        if (wordCount == 0)
            return CaptionStyle.Unknown;

        var commaRatio = (double)commaCount / wordCount;
        var hasUnderscoreTokens = words.Any(w => w.Contains('_'));

        // Count articles as strong NL signal
        var articleCount = words.Count(w =>
            w.Equals("a", StringComparison.OrdinalIgnoreCase) ||
            w.Equals("an", StringComparison.OrdinalIgnoreCase) ||
            w.Equals("the", StringComparison.OrdinalIgnoreCase));
        var articleRatio = (double)articleCount / wordCount;

        var hasSentences = periodCount >= 1 && trimmed.Length > 30;
        var hasHighCommaDensity = commaRatio > 0.3;
        var hasArticles = articleRatio > 0.04;

        // Strong booru signals
        if (hasHighCommaDensity && periodCount == 0 && !hasArticles)
            return CaptionStyle.BooruTags;

        if (hasUnderscoreTokens && commaCount >= 2 && !hasSentences)
            return CaptionStyle.BooruTags;

        // Strong NL signals
        if (hasSentences && !hasHighCommaDensity)
            return CaptionStyle.NaturalLanguage;

        if (hasArticles && periodCount >= 1)
            return CaptionStyle.NaturalLanguage;

        // Mixed signals
        if (hasSentences && hasHighCommaDensity)
            return CaptionStyle.Mixed;

        // Fallback heuristics
        if (commaCount >= 2 && periodCount == 0)
            return CaptionStyle.BooruTags;

        if (periodCount >= 1)
            return CaptionStyle.NaturalLanguage;

        return CaptionStyle.Unknown;
    }

    #endregion

    #region Phrase / Feature Search

    /// <summary>
    /// Word-boundary-aware phrase search (case-insensitive).
    /// "car" matches "a car is parked" but NOT "scar" or "boxcar".
    /// </summary>
    public static bool ContainsPhrase(string text, string phrase)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(phrase))
            return false;

        return WordBoundaryRegex(Regex.Escape(phrase)).IsMatch(text);
    }

    /// <summary>
    /// Style-aware feature search.
    /// <list type="bullet">
    /// <item><b>BooruTags</b>: exact tag match (comma-separated, trimmed, case-insensitive)</item>
    /// <item><b>NaturalLanguage / Mixed / Unknown</b>: word-boundary match</item>
    /// </list>
    /// </summary>
    public static bool ContainsFeature(string caption, string feature, CaptionStyle style)
    {
        if (string.IsNullOrEmpty(caption) || string.IsNullOrEmpty(feature))
            return false;

        if (style == CaptionStyle.BooruTags)
        {
            var tags = SplitTags(caption);
            return tags.Any(t => t.Equals(feature, StringComparison.OrdinalIgnoreCase));
        }

        // NL / Mixed / Unknown — fall back to word-boundary search
        return ContainsPhrase(caption, feature);
    }

    #endregion

    #region Token Extraction

    /// <summary>
    /// Extracts a cleaned, lowercased word list from a caption.
    /// Strips punctuation, removes stop words, and splits booru underscores.
    /// </summary>
    public static List<string> ExtractTokens(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return [];

        // Normalise: replace underscores with spaces, strip non-letter/space chars
        var normalised = UnderscorePattern.Replace(caption, " ");
        normalised = NonWordPattern.Replace(normalised, " ");

        var words = normalised
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length > 1 && !StopWords.Set.Contains(w))
            .ToList();

        return words;
    }

    /// <summary>
    /// Produces ordered bigrams from a token list.
    /// </summary>
    public static List<string> ExtractBigrams(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2)
            return [];

        var bigrams = new List<string>(tokens.Count - 1);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            bigrams.Add($"{tokens[i]} {tokens[i + 1]}");
        }
        return bigrams;
    }

    /// <summary>
    /// Produces ordered trigrams from a token list.
    /// </summary>
    public static List<string> ExtractTrigrams(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 3)
            return [];

        var trigrams = new List<string>(tokens.Count - 2);
        for (var i = 0; i < tokens.Count - 2; i++)
        {
            trigrams.Add($"{tokens[i]} {tokens[i + 1]} {tokens[i + 2]}");
        }
        return trigrams;
    }

    #endregion

    #region Phrase Manipulation

    /// <summary>
    /// Removes a phrase from a caption in a style-aware manner.
    /// <list type="bullet">
    /// <item><b>BooruTags</b>: removes the exact tag and cleans up surrounding commas.</item>
    /// <item><b>NaturalLanguage / other</b>: word-boundary removal with whitespace cleanup.</item>
    /// </list>
    /// </summary>
    public static string RemovePhrase(string caption, string phrase, CaptionStyle style)
    {
        if (string.IsNullOrEmpty(caption) || string.IsNullOrEmpty(phrase))
            return caption ?? string.Empty;

        if (style == CaptionStyle.BooruTags)
        {
            var tags = SplitTags(caption);
            var filtered = tags
                .Where(t => !t.Equals(phrase, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return string.Join(", ", filtered);
        }

        // NL / Mixed / Unknown — word-boundary removal
        var result = WordBoundaryRegex(Regex.Escape(phrase)).Replace(caption, "");
        return CollapseWhitespace(result);
    }

    /// <summary>
    /// Appends a phrase to a caption in a style-aware manner.
    /// <list type="bullet">
    /// <item><b>BooruTags</b>: appends as a new comma-separated tag.</item>
    /// <item><b>NaturalLanguage / other</b>: appends to the end of the text with proper spacing.</item>
    /// </list>
    /// </summary>
    public static string AppendPhrase(string caption, string phrase, CaptionStyle style)
    {
        if (string.IsNullOrEmpty(phrase))
            return caption ?? string.Empty;

        if (string.IsNullOrWhiteSpace(caption))
            return phrase;

        var trimmedCaption = caption.TrimEnd();

        if (style == CaptionStyle.BooruTags)
        {
            // Ensure no trailing comma before appending
            trimmedCaption = trimmedCaption.TrimEnd(',').TrimEnd();
            return $"{trimmedCaption}, {phrase}";
        }

        // NL / Mixed / Unknown — append with space, add period if needed
        if (trimmedCaption.Length > 0 && !char.IsPunctuation(trimmedCaption[^1]))
        {
            trimmedCaption += ".";
        }

        return $"{trimmedCaption} {phrase}";
    }

    #endregion

    #region Internal Helpers

    /// <summary>
    /// Splits a booru-style caption into individual trimmed tags.
    /// </summary>
    internal static List<string> SplitTags(string caption) =>
        caption
            .Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>
    /// Collapses multiple whitespace characters into a single space and trims.
    /// </summary>
    private static string CollapseWhitespace(string text) =>
        MultiSpacePattern.Replace(text, " ").Trim();

    private static int CountChar(string text, char c)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch == c) count++;
        }
        return count;
    }

    /// <summary>
    /// Creates a case-insensitive word-boundary regex for the given escaped pattern.
    /// </summary>
    private static Regex WordBoundaryRegex(string escapedPattern) =>
        new($@"\b{escapedPattern}\b", RegexOptions.IgnoreCase);

    private static readonly Regex UnderscorePattern = new(@"_", RegexOptions.Compiled);
    private static readonly Regex NonWordPattern = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpacePattern = new(@"\s{2,}", RegexOptions.Compiled);

    #endregion
}
