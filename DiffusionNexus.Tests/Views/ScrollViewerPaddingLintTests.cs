using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DiffusionNexus.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Views;

/// <summary>
/// Guards against <c>Padding</c> on a <c>ScrollViewer</c> in any .axaml file of the repository.
/// <para>
/// Avalonia's <c>ScrollContentPresenter</c> (verified on 11.3.11, 11.3.13, 11.3.18 and 12.0.3)
/// arranges a stretchable child <em>2×Padding shorter</em> than its measured height and reports
/// an <c>Extent</c> that omits the padding entirely. Net effect: once the content is taller than
/// the viewport, the last <c>3×vertical-padding</c> pixels can never be scrolled into view — the
/// Dataset Management image cards lost their caption Save/Undo/Reset row and the LoRA Viewer
/// tiles lost their hover action row on the bottom row. Putting the same inset on the scrolled
/// content as <c>Margin</c> is honoured by the extent and produces the identical visual result.
/// </para>
/// <para>
/// Three routes to a padded ScrollViewer are rejected: the <c>Padding="…"</c> attribute, the
/// property-element form <c>&lt;ScrollViewer.Padding&gt;</c>, and a <c>Setter Property="Padding"</c>
/// inside a <c>Style</c> / <c>ControlTheme</c> whose selector or target type names ScrollViewer
/// (a theme-level setter would re-pad every ScrollViewer in the app at once).
/// </para>
/// <para>
/// <c>DiffusionNexus.IntegrationTests.ScrollViewerPaddingBehaviourTests</c> pins the Avalonia
/// behaviour itself; if that canary ever flips (upstream fixed it), this lint can be retired.
/// </para>
/// </summary>
public class ScrollViewerPaddingLintTests
{
    private static readonly Regex ScrollViewerWord = new(@"\bScrollViewer\b", RegexOptions.Compiled);

    [Fact]
    public void No_axaml_ScrollViewer_uses_Padding()
    {
        var repoRoot = RepoRoot.Path;
        var offenders = Directory.EnumerateFiles(repoRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .SelectMany(path => FindPaddedScrollViewers(path).Select(line => $"{Path.GetRelativePath(repoRoot, path)}:{line}"))
            .ToList();

        // Joined so the failure lists every site, not just FluentAssertions' first item.
        string.Join(Environment.NewLine, offenders).Should().BeEmpty(
            "ScrollViewer.Padding makes the bottom 3×padding of scrolled content unreachable in Avalonia; " +
            "put the inset on the ScrollViewer's content as Margin instead (see class remarks)");
    }

    [Fact]
    public void Lint_detects_attribute_property_element_and_style_setter_forms()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dn-lint-{Guid.NewGuid():N}.axaml");
        File.WriteAllText(path, """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <UserControl.Styles>
                <Style Selector="ScrollViewer.tiles">
                  <Setter Property="Padding" Value="16"/>
                </Style>
                <Style Selector="ListBox">
                  <Setter Property="Padding" Value="16"/>
                </Style>
                <ControlTheme x:Key="Padded" TargetType="{x:Type ScrollViewer}">
                  <Setter Property="Padding" Value="8"/>
                </ControlTheme>
              </UserControl.Styles>
              <StackPanel>
                <ScrollViewer Padding="16"><Border/></ScrollViewer>
                <ScrollViewer><ScrollViewer.Padding>16</ScrollViewer.Padding><Border/></ScrollViewer>
                <ScrollViewer><Border Margin="16"/></ScrollViewer>
                <Border Padding="16"/>
              </StackPanel>
            </UserControl>
            """);
        try
        {
            FindPaddedScrollViewers(path).Should().Equal(new[] { 4, 10, 14, 15 },
                "the ScrollViewer style setter, the ScrollViewer control-theme setter, the attribute and the " +
                "property-element form are all offenders; the ListBox setter, the Margin and the Border are not");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<int> FindPaddedScrollViewers(string path)
    {
        var doc = XDocument.Load(path, LoadOptions.SetLineInfo);

        foreach (var element in doc.Descendants())
        {
            var name = element.Name.LocalName;

            // <ScrollViewer Padding="…"> and <ScrollViewer><ScrollViewer.Padding>…
            if (name == "ScrollViewer" && element.Attribute("Padding") is not null)
            {
                yield return LineOf(element);
            }

            if (name == "ScrollViewer.Padding")
            {
                yield return LineOf(element);
            }

            // <Style Selector="…ScrollViewer…"> / <ControlTheme TargetType="ScrollViewer"> with <Setter Property="Padding">
            if (name is "Style" or "ControlTheme" && TargetsScrollViewer(element))
            {
                foreach (var setter in element.Elements().Where(e => e.Name.LocalName == "Setter"))
                {
                    if (setter.Attribute("Property")?.Value == "Padding")
                    {
                        yield return LineOf(setter);
                    }
                }
            }
        }
    }

    private static bool TargetsScrollViewer(XElement styleOrTheme)
    {
        var selector = styleOrTheme.Attribute("Selector")?.Value;
        var targetType = styleOrTheme.Attribute("TargetType")?.Value; // "ScrollViewer" or "{x:Type ScrollViewer}"
        return (selector is not null && ScrollViewerWord.IsMatch(selector))
            || (targetType is not null && ScrollViewerWord.IsMatch(targetType));
    }

    private static int LineOf(XElement element) => ((IXmlLineInfo)element).LineNumber;

    private static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}") || path.Contains($"{sep}obj{sep}");
    }

}
