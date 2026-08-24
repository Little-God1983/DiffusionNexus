using DiffusionNexus.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Domain;

public class FileFormatMapperTests
{
    [Theory]
    [InlineData(".safetensors", FileFormat.SafeTensor)]
    [InlineData(".SAFETENSORS", FileFormat.SafeTensor)]
    [InlineData(".pt", FileFormat.PickleTensor)]
    [InlineData(".pth", FileFormat.PickleTensor)]
    [InlineData(".ckpt", FileFormat.Other)]
    [InlineData(".gguf", FileFormat.Unknown)]
    [InlineData("", FileFormat.Unknown)]
    public void FromExtension_MatchesTheThreeFormerCopies(string extension, FileFormat expected)
        => FileFormatMapper.FromExtension(extension).Should().Be(expected);
}
