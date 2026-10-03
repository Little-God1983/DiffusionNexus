using Avalonia.Input;
using DiffusionNexus.UI.DiffusionCanvas;
using DiffusionNexus.UI.Views.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>Which keys resize the brush (#595 review), and the size rule they share.</summary>
public class CanvasBrushKeyTests
{
    private static KeyEventArgs Key(Key key, string? symbol) => new() { Key = key, KeySymbol = symbol };

    [Theory]
    [InlineData(Avalonia.Input.Key.OemCloseBrackets, "]", true)]
    [InlineData(Avalonia.Input.Key.OemOpenBrackets, "[", false)]
    [InlineData(Avalonia.Input.Key.D9, "]", true)]                    // German: AltGr+9
    [InlineData(Avalonia.Input.Key.D8, "[", false)]                   // German: AltGr+8
    [InlineData(Avalonia.Input.Key.OemCloseBrackets, null, true)]     // no symbol: fall back to the US key
    public void TheBracketsResizeTheBrush(Key key, string? symbol, bool grow)
    {
        DiffusionCanvasView.BrushStep(Key(key, symbol)).Should().Be(grow);
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.OemOpenBrackets, "ß")]             // German: the US [ key types ß
    [InlineData(Avalonia.Input.Key.OemCloseBrackets, "´")]            // German: the US ] key is the ´ dead key
    [InlineData(Avalonia.Input.Key.A, "a")]
    public void OtherCharactersOnThoseKeysDoNot(Key key, string symbol)
    {
        DiffusionCanvasView.BrushStep(Key(key, symbol)).Should().BeNull();
    }

    [Theory]
    [InlineData(512, true, 512)]
    [InlineData(4, false, 4)]
    [InlineData(0, true, 4)]
    [InlineData(64, true, 80)]
    public void AStepIsAlwaysWithinTheLimits(double size, bool grow, double expected)
    {
        CanvasBrush.Step(size, grow).Should().Be(expected);
    }
}
