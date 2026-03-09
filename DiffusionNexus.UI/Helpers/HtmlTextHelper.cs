using System.Text;
using System.Text.RegularExpressions;

namespace DiffusionNexus.UI.Helpers;

/// <summary>
/// Converts simple HTML (as returned by Civitai model descriptions) into readable plain text.
/// Handles common block/inline elements and decodes HTML entities.
/// </summary>
internal static partial class HtmlTextHelper
{
    /// <summary>
    /// Converts HTML markup into plain text with appropriate line breaks.
    /// </summary>
    /// <param name="html">Raw HTML string.</param>
    /// <returns>Readable plain text.</returns>
    public static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = html;

        // Replace <br> and <br/> with newline
        text = BrTagRegex().Replace(text, "\n");

        // Replace closing block tags with double newline
        text = BlockCloseTagRegex().Replace(text, "\n\n");

        // Replace opening block tags (removes them)
        text = BlockOpenTagRegex().Replace(text, string.Empty);

        // Replace <li> with bullet
        text = ListItemRegex().Replace(text, "\n • ");

        // Strip all remaining HTML tags
        text = AnyTagRegex().Replace(text, string.Empty);

        // Decode common HTML entities
        text = text
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&#39;", "'")
            .Replace("&nbsp;", " ");

        // Decode numeric entities (&#123; or &#x1F;)
        text = NumericEntityRegex().Replace(text, m =>
        {
            var value = m.Groups[1].Value;
            if (int.TryParse(value, out var code))
                return ((char)code).ToString();
            return m.Value;
        });

        text = HexEntityRegex().Replace(text, m =>
        {
            var value = m.Groups[1].Value;
            if (int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var code))
                return ((char)code).ToString();
            return m.Value;
        });

        // Normalize whitespace: collapse multiple spaces (not newlines) into one
        text = MultiSpaceRegex().Replace(text, " ");

        // Collapse 3+ consecutive newlines into 2
        text = ExcessiveNewlinesRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagRegex();

    [GeneratedRegex(@"</(?:p|div|h[1-6]|ul|ol|li|blockquote|pre|table|tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockCloseTagRegex();

    [GeneratedRegex(@"<(?:p|div|h[1-6]|ul|ol|blockquote|pre|table|tr)(?:\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockOpenTagRegex();

    [GeneratedRegex(@"<li(?:\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"&#(\d+);")]
    private static partial Regex NumericEntityRegex();

    [GeneratedRegex(@"&#x([0-9a-fA-F]+);")]
    private static partial Regex HexEntityRegex();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlinesRegex();
}
