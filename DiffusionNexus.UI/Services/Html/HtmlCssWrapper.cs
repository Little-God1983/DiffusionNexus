using System.Text;

namespace DiffusionNexus.UI.Services.Html;

public class HtmlCssWrapper
{
    private readonly string _lightCss = BuildCss(false);
    private readonly string _darkCss = BuildCss(true);

    public string Wrap(string bodyContent, bool isDarkTheme)
    {
        var css = isDarkTheme ? _darkCss : _lightCss;
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html>");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\" />");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        builder.AppendLine("<style>");
        builder.AppendLine(css);
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine(bodyContent);
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static string BuildCss(bool isDark)
    {
        var background = isDark ? "#1E1E1E" : "#FFFFFF";
        var foreground = isDark ? "#E0E0E0" : "#1F1F1F";
        var link = isDark ? "#7BA9FF" : "#0A5CCF";
        var border = isDark ? "#3A3A3A" : "#D0D0D0";
        var codeBackground = isDark ? "#2A2A2A" : "#F5F5F5";
        var builder = new StringBuilder();
        builder.AppendLine(":root {");
        builder.AppendLine($"    color-scheme: {(isDark ? "dark" : "light")};");
        builder.AppendLine("}");
        builder.AppendLine("body {");
        builder.AppendLine("    margin: 0;");
        builder.AppendLine("    padding: 0 12px 12px 12px;");
        builder.AppendLine("    font-family: 'Inter', 'Segoe UI', sans-serif;");
        builder.AppendLine("    font-size: 14px;");
        builder.AppendLine("    line-height: 1.6;");
        builder.AppendLine($"    background-color: {background};");
        builder.AppendLine($"    color: {foreground};");
        builder.AppendLine("}");
        builder.AppendLine("h1, h2, h3, h4 {");
        builder.AppendLine("    margin-top: 1.2em;");
        builder.AppendLine("    margin-bottom: 0.6em;");
        builder.AppendLine("    font-weight: 600;");
        builder.AppendLine("}");
        builder.AppendLine("p {");
        builder.AppendLine("    margin-top: 0.6em;");
        builder.AppendLine("    margin-bottom: 0.6em;");
        builder.AppendLine("}");
        builder.AppendLine("ul, ol {");
        builder.AppendLine("    margin: 0.6em 0 0.6em 1.4em;");
        builder.AppendLine("    padding: 0;");
        builder.AppendLine("}");
        builder.AppendLine("a {");
        builder.AppendLine($"    color: {link};");
        builder.AppendLine("    text-decoration: underline;");
        builder.AppendLine("}");
        builder.AppendLine("a:hover {");
        builder.AppendLine("    text-decoration: none;");
        builder.AppendLine("}");
        builder.AppendLine("table {");
        builder.AppendLine("    border-collapse: collapse;");
        builder.AppendLine("    width: 100%;");
        builder.AppendLine("    margin: 0.6em 0;");
        builder.AppendLine("}");
        builder.AppendLine("th, td {");
        builder.AppendLine($"    border: 1px solid {border};");
        builder.AppendLine("    padding: 6px;");
        builder.AppendLine("    text-align: left;");
        builder.AppendLine("}");
        builder.AppendLine("img {");
        builder.AppendLine("    max-width: 100%;");
        builder.AppendLine("    height: auto;");
        builder.AppendLine("    border-radius: 4px;");
        builder.AppendLine("}");
        builder.AppendLine("code, pre {");
        builder.AppendLine("    font-family: 'Cascadia Code', 'Consolas', monospace;");
        builder.AppendLine("}");
        builder.AppendLine("pre {");
        builder.AppendLine($"    background-color: {codeBackground};");
        builder.AppendLine("    padding: 10px;");
        builder.AppendLine("    border-radius: 6px;");
        builder.AppendLine("    overflow: auto;");
        builder.AppendLine("}");
        builder.AppendLine("code {");
        builder.AppendLine($"    background-color: {codeBackground};");
        builder.AppendLine("    padding: 2px 4px;");
        builder.AppendLine("    border-radius: 4px;");
        builder.AppendLine("}");
        builder.AppendLine(".html-placeholder {");
        builder.AppendLine("    font-style: italic;");
        builder.AppendLine($"    color: {border};");
        builder.AppendLine("    padding: 12px 0;");
        builder.AppendLine("}");
        return builder.ToString();
    }
}
