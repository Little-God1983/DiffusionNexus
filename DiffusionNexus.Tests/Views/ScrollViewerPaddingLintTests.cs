using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Views;

/// <summary>
/// Guards against <c>Padding</c> on a <c>ScrollViewer</c> in any UI .axaml file.
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
/// <c>DiffusionNexus.IntegrationTests.ScrollViewerPaddingBehaviourTests</c> pins the Avalonia
/// behaviour itself; if that canary ever flips (upstream fixed it), this lint can be retired.
/// </para>
/// </summary>
public class ScrollViewerPaddingLintTests
{
    private static readonly Regex ScrollViewerWithPadding = new(
        @"<ScrollViewer\b[^>]*?\sPadding=""[^""]*""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void No_axaml_ScrollViewer_uses_Padding()
    {
        var uiRoot = FindUiProjectRoot();
        var offenders = Directory.EnumerateFiles(uiRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return ScrollViewerWithPadding.Matches(text)
                    .Select(m => $"{Path.GetRelativePath(uiRoot, path)}:{LineOf(text, m.Index)}");
            })
            .ToList();

        // Joined so the failure lists every site, not just FluentAssertions' first item.
        string.Join(Environment.NewLine, offenders).Should().BeEmpty(
            "ScrollViewer.Padding makes the bottom 3×padding of scrolled content unreachable in Avalonia; " +
            "put the inset on the ScrollViewer's content as Margin instead (see class remarks)");
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string FindUiProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "DiffusionNexus.UI", "DiffusionNexus.UI.csproj");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DiffusionNexus.UI above " + AppContext.BaseDirectory);
    }
}
