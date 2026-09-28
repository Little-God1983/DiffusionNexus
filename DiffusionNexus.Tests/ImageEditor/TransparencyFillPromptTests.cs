using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// <c>OptionsDialog</c> styles its buttons by position: the first is the neutral dismiss button,
/// the last the green primary. The prompt's buttons and the index → fill mapping have to agree
/// with that, or the dialog recommends Cancel and the wrong choice gets saved.
/// </summary>
public class TransparencyFillPromptTests
{
    private static readonly IReadOnlyList<string> Options = TransparencyFillPrompt.Options;

    [Fact]
    public void WhenTheFirstButtonIsPickedThenTheSaveIsCancelled()
    {
        TransparencyFillPrompt.FromChoice(0).Should().BeNull();
    }

    [Fact]
    public void WhenTheGreenPrimaryButtonIsPickedThenTheFillIsWhite()
    {
        TransparencyFillPrompt.FromChoice(Options.Count - 1).Should().Be(TransparencyFill.White);
    }

    [Theory]
    [InlineData(TransparencyFill.White)]
    [InlineData(TransparencyFill.Black)]
    public void WhenAFillButtonIsPickedThenItsLabelNamesThatFill(TransparencyFill fill)
    {
        var index = Enumerable.Range(0, Options.Count).Single(i => TransparencyFillPrompt.FromChoice(i) == fill);

        Options[index].Should().ContainEquivalentOf(fill.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void WhenTheDialogIsClosedOrAnswersOutOfRangeThenTheSaveIsCancelled(int choice)
    {
        TransparencyFillPrompt.FromChoice(choice).Should().BeNull();
    }
}
