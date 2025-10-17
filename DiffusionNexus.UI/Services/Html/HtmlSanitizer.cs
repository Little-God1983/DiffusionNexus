using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;

namespace DiffusionNexus.UI.Services.Html;

public class HtmlSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "p", "ul", "ol", "li", "h1", "h2", "h3", "h4", "img", "table", "thead", "tbody", "tr",
        "th", "td", "code", "pre", "strong", "em", "b", "i", "span", "div", "br", "hr"
    };

    private static readonly Dictionary<string, HashSet<string>> TagSpecificAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href", "title", "target", "rel" },
        ["img"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "alt", "title", "width", "height" },
        ["table"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "border", "cellpadding", "cellspacing" },
        ["th"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "colspan", "rowspan" },
        ["td"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "colspan", "rowspan" },
        ["code"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "class" },
        ["pre"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "class" }
    };

    private static readonly HashSet<string> GlobalAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "class"
    };

    private static readonly HashSet<string> AllowedStyleProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "background-color", "font-weight", "font-style", "text-decoration", "text-align",
        "font-size", "line-height", "margin", "margin-left", "margin-right", "margin-top", "margin-bottom",
        "padding", "padding-left", "padding-right", "padding-top", "padding-bottom", "border", "border-color",
        "border-width", "border-style", "border-radius", "display", "list-style-type"
    };

    public string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);
        SanitizeNode(document.DocumentNode);
        return document.DocumentNode.InnerHtml;
    }

    private void SanitizeNode(HtmlNode node)
    {
        if (node.NodeType == HtmlNodeType.Element)
        {
            var tagName = node.Name;
            if (!AllowedTags.Contains(tagName))
            {
                PromoteChildrenAndRemove(node);
                return;
            }

            SanitizeAttributes(node);
        }

        foreach (var child in node.ChildNodes.ToList())
        {
            SanitizeNode(child);
        }
    }

    private void SanitizeAttributes(HtmlNode node)
    {
        foreach (var attribute in node.Attributes.ToList())
        {
            if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                node.Attributes.Remove(attribute);
                continue;
            }

            if (string.Equals(attribute.Name, "style", StringComparison.OrdinalIgnoreCase))
            {
                var sanitized = SanitizeStyle(attribute.Value);
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    node.Attributes.Remove(attribute);
                }
                else
                {
                    attribute.Value = sanitized;
                }

                continue;
            }

            if (string.Equals(attribute.Name, "href", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSafeLink(attribute.Value))
                {
                    node.Attributes.Remove(attribute);
                }
                else
                {
                    attribute.Value = attribute.Value.Trim();
                }

                continue;
            }

            if (string.Equals(attribute.Name, "src", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSafeImageSource(attribute.Value))
                {
                    node.Attributes.Remove(attribute);
                }
                else
                {
                    attribute.Value = attribute.Value.Trim();
                }

                continue;
            }

            if (!IsAttributeAllowed(node.Name, attribute.Name))
            {
                node.Attributes.Remove(attribute);
            }
        }
    }

    private static void PromoteChildrenAndRemove(HtmlNode node)
    {
        if (node.ParentNode == null)
        {
            node.Remove();
            return;
        }

        foreach (var child in node.ChildNodes.ToList())
        {
            node.ParentNode.InsertBefore(child, node);
        }

        node.ParentNode.RemoveChild(node);
    }

    private static bool IsAttributeAllowed(string tagName, string attributeName)
    {
        if (GlobalAttributes.Contains(attributeName))
        {
            return true;
        }

        return TagSpecificAttributes.TryGetValue(tagName, out var attributes) && attributes.Contains(attributeName);
    }

    private static bool IsSafeLink(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var value = href.Trim();
        if (value.StartsWith("#"))
        {
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.Scheme is "http" or "https";
        }

        return Uri.TryCreate(value, UriKind.Relative, out _);
    }

    private static bool IsSafeImageSource(string? src)
    {
        if (string.IsNullOrWhiteSpace(src))
        {
            return false;
        }

        var value = src.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https";
    }

    private string SanitizeStyle(string value)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sanitized = new List<string>();

        foreach (var part in parts)
        {
            var kvp = part.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (kvp.Length != 2)
            {
                continue;
            }

            var property = kvp[0].ToLowerInvariant();
            if (!AllowedStyleProperties.Contains(property))
            {
                continue;
            }

            var propertyValue = kvp[1].Trim();
            var lowerValue = propertyValue.ToLowerInvariant();
            if (lowerValue.Contains("expression") || lowerValue.Contains("javascript") || lowerValue.Contains("url("))
            {
                continue;
            }

            sanitized.Add($"{property}: {propertyValue}");
        }

        return string.Join("; ", sanitized);
    }
}
