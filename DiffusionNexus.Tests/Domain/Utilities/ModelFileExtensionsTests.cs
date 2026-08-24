using DiffusionNexus.Domain.Utilities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain.Utilities;

/// <summary>
/// The lists these replaced disagreed seven ways. Merging them into one flat set fixed that and
/// promptly caused the opposite bug — the sorter, which MOVES everything its enumeration yields,
/// inherited <c>.bin</c> and <c>.gguf</c>. These pin the split that keeps both properties: one set
/// for what may be relocated, a wider one for what may merely be recognized by name.
/// </summary>
public sealed class ModelFileExtensionsTests
{
    /// <summary>
    /// Recognizing is advisory, moving is not. Anything the app is willing to relocate must also be
    /// something it is willing to call a model — the reverse must not hold.
    /// </summary>
    [Fact]
    public void SortableIsASubsetOfRecognized()
        => ModelFileExtensions.Recognized.Should().Contain(ModelFileExtensions.Sortable);

    /// <summary>
    /// The regression this file exists for: a root holding <c>pytorch_model.bin</c> or a quantized
    /// <c>.gguf</c> must not have them filed into base-model folders. The asset-kind classifier is
    /// name-based, so they would arrive wearing a [LoRA] chip with nothing to flag them.
    /// </summary>
    [Theory]
    [InlineData(".bin")]
    [InlineData(".gguf")]
    public void WeightFormatsWeDoNotMoveAreRecognizedButNotSortable(string extension)
    {
        ModelFileExtensions.Recognized.Should().Contain(extension);
        ModelFileExtensions.Sortable.Should().NotContain(extension,
            "everything Sortable yields becomes a planned move");
    }

    /// <summary>A safetensors container is a model file the app will move, under either spelling.</summary>
    [Fact]
    public void EverySafetensorsContainerIsSortable()
        => ModelFileExtensions.Sortable.Should().Contain(ModelFileExtensions.SafetensorsContainers);

    /// <summary>
    /// ".sft" is the short spelling of the container the header reader has always read. A library
    /// that could not discover one while its own identity chain could read it was inconsistent, not
    /// conservative — and the sorter could file a model the Viewer would never show.
    /// </summary>
    [Fact]
    public void TheShortSafetensorsSpellingIsSortable()
        => ModelFileExtensions.Sortable.Should().Contain(".sft");

    [Theory]
    [InlineData("model.SAFETENSORS", true)]
    [InlineData("model.Sft", true)]
    [InlineData("notes.txt", false)]
    [InlineData("model", false)]
    public void MatchesIsCaseInsensitive(string fileName, bool expected)
        => ModelFileExtensions.Matches(fileName, ModelFileExtensions.Sortable).Should().Be(expected);
}
