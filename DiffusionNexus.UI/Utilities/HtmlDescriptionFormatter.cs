using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Styling;
using Ganss.Xss;

namespace DiffusionNexus.UI.Utilities;

internal static class HtmlDescriptionFormatter
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();
    private static readonly string LightCss = BuildCss(ThemeVariant.Light);
    private static readonly string DarkCss = BuildCss(ThemeVariant.Dark);

    public static string? Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var sanitized = Sanitizer.Sanitize(html);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized.Trim();
    }

    public static string BuildDocument(string? sanitizedHtml, ThemeVariant themeVariant)
    {
        var css = themeVariant == ThemeVariant.Dark ? DarkCss : LightCss;
        var colorScheme = themeVariant == ThemeVariant.Dark ? "dark" : "light";
        var bodyContent = string.IsNullOrWhiteSpace(sanitizedHtml)
            ? "<p class=\"placeholder\">No description provided.</p>"
            : sanitizedHtml;

        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\" />");
        builder.AppendLine("  <meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src https: data:; style-src 'unsafe-inline';\">");
        builder.Append("  <meta name=\"color-scheme\" content=\"").Append(colorScheme).AppendLine("\" />");
        builder.AppendLine("  <style>");
        builder.AppendLine(css);
        builder.AppendLine("  </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("  <main class=\"content\">");
        builder.AppendLine(bodyContent);
        builder.AppendLine("  </main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    public static string GetStylesheet(ThemeVariant themeVariant) => themeVariant == ThemeVariant.Dark ? DarkCss : LightCss;

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("http");

        sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedTags)
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in AllowedAttributes)
        {
            sanitizer.AllowedAttributes.Add(attribute);
        }

        sanitizer.AllowedCssProperties.Clear();
        foreach (var property in AllowedCssProperties)
        {
            sanitizer.AllowedCssProperties.Add(property);
        }

        sanitizer.AllowDataAttributes = false;
        sanitizer.AllowCssCustomProperties = false;
        sanitizer.FilterUrl += OnFilterUrl;
        return sanitizer;
    }

    private static void OnFilterUrl(object? sender, FilterUrlEventArgs e)
    {
        var url = e.SanitizedUrl ?? e.OriginalUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            e.SanitizedUrl = null;
            return;
        }

        var tagName = e.Tag?.TagName?.ToLowerInvariant();
        if (tagName == "img")
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = null;
            }

            return;
        }

        if (tagName == "a")
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = url;
            }
            else
            {
                e.SanitizedUrl = null;
            }

            return;
        }

        e.SanitizedUrl = null;
    }

    private static string BuildCss(ThemeVariant themeVariant)
    {
        var isDark = themeVariant == ThemeVariant.Dark;
        var background = isDark ? "#1B1D1F" : "#FFFFFF";
        var foreground = isDark ? "#E6E8EA" : "#1B1D1F";
        var secondary = isDark ? "#C0C5CC" : "#3C4043";
        var border = isDark ? "#2F3336" : "#D0D7DE";
        var link = isDark ? "#8AB4F8" : "#1A73E8";
        var codeBackground = isDark ? "#2A2D31" : "#F5F7FA";

        var builder = new StringBuilder();
        builder.AppendLine(":root { font-family: 'Inter', 'Segoe UI', sans-serif; }");
        builder.AppendLine("body { margin: 0; padding: 0; background: " + background + "; color: " + foreground + "; font-size: 14px; line-height: 1.55; }");
        builder.AppendLine(".content { padding: 0.75rem 1rem; background: " + background + "; color: inherit; }");
        builder.AppendLine("h1, h2, h3, h4 { margin: 1.2rem 0 0.5rem; font-weight: 600; line-height: 1.25; }");
        builder.AppendLine("p { margin: 0 0 0.75rem; color: inherit; }");
        builder.AppendLine("ul, ol { margin: 0 0 0.75rem 1.35rem; padding: 0; }");
        builder.AppendLine("li { margin-bottom: 0.35rem; }");
        builder.AppendLine("a { color: " + link + "; text-decoration: none; }");
        builder.AppendLine("a:hover, a:focus { text-decoration: underline; }");
        builder.AppendLine("img { max-width: 100%; border-radius: 6px; margin: 0.5rem 0; }");
        builder.AppendLine("table { border-collapse: collapse; width: 100%; margin-bottom: 1rem; }");
        builder.AppendLine("th, td { border: 1px solid " + border + "; padding: 0.5rem; text-align: left; vertical-align: top; }");
        builder.AppendLine("blockquote { margin: 0.75rem 0; padding: 0.5rem 0.75rem; border-left: 3px solid " + border + "; color: " + secondary + "; }");
        builder.AppendLine("code { background: " + codeBackground + "; padding: 0.15rem 0.4rem; border-radius: 4px; font-family: 'Cascadia Code', 'Consolas', monospace; font-size: 0.95em; }");
        builder.AppendLine("pre { background: " + codeBackground + "; padding: 0.75rem; border-radius: 6px; overflow-x: auto; }");
        builder.AppendLine("pre code { background: transparent; padding: 0; }");
        builder.AppendLine(".placeholder { color: " + secondary + "; font-style: italic; text-align: center; margin: 3rem 0; }");
        return builder.ToString();
    }

    private static readonly IReadOnlyCollection<string> AllowedTags = new[]
    {
        "a", "abbr", "b", "blockquote", "br", "code", "div", "em", "h1", "h2", "h3", "h4",
        "hr", "i", "img", "li", "ol", "p", "pre", "span", "strong", "sub", "sup", "table",
        "tbody", "td", "th", "thead", "tr", "u", "ul"
    };

    private static readonly IReadOnlyCollection<string> AllowedAttributes = new[]
    {
        "href", "src", "alt", "title", "style", "class", "width", "height"
    };

    private static readonly IReadOnlyCollection<string> AllowedCssProperties = new[]
    {
        "color", "background-color", "font-style", "font-weight", "text-decoration", "text-align",
        "margin", "margin-left", "margin-right", "margin-top", "margin-bottom",
        "padding", "padding-left", "padding-right", "padding-top", "padding-bottom",
        "border", "border-left", "border-right", "border-top", "border-bottom",
        "border-color", "border-width", "border-style", "border-radius",
        "display", "list-style", "list-style-type", "vertical-align", "width", "height"
    };
}
