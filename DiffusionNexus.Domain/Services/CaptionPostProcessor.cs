using System.Text.RegularExpressions;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Shared caption post-processing utilities used by all captioning backends.
/// </summary>
public static class CaptionPostProcessor
{
    /// <summary>
    /// Applies trigger word prepending and blacklisted word removal to a raw caption.
    /// </summary>
    /// <param name="caption">The raw caption text.</param>
    /// <param name="triggerWord">Optional token to prepend.</param>
    /// <param name="blacklistedWords">Words to remove (case-insensitive, whole-word match).</param>
    /// <returns>The processed caption.</returns>
    public static string Process(
        string caption,
        string? triggerWord = null,
        IReadOnlyList<string>? blacklistedWords = null)
    {
        var result = caption.Trim();

        // Remove blacklisted words
        if (blacklistedWords is { Count: > 0 })
        {
            foreach (var word in blacklistedWords)
            {
                result = Regex.Replace(
                    result,
                    $@"\b{Regex.Escape(word)}\b",
                    "",
                    RegexOptions.IgnoreCase);
            }

            // Clean up extra whitespace after removals
            result = Regex.Replace(result, @"\s+", " ").Trim();
        }

        // Prepend trigger word if specified
        if (!string.IsNullOrWhiteSpace(triggerWord))
        {
            result = $"{triggerWord.Trim()}, {result}";
        }

        return result;
    }
}
