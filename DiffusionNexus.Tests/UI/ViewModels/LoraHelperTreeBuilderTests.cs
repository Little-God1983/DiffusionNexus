using System.Collections.Generic;
using System.Linq;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.UI.ViewModels;

public class LoraHelperTreeBuilderTests
{
    [Fact]
    public void BuildMergedSegments_UsesBaseModelAndRelativePath()
    {
        var sourcePath = "D:/Matrix/Models/My Models";
        var folderPath = "D:/Matrix/Models/My Models/Flux.1 D/SubFolder";

        var result = LoraHelperTreeBuilder.BuildMergedSegments(sourcePath, folderPath, "Flux.1 D");

        result.Should().Equal(
            new[]
            {
                LoraHelperTreeBuilder.BaseLoraRootName,
                "Flux.1 D",
                "SubFolder"
            });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UNKNOWN")]
    public void BuildMergedSegments_UsesUnknownFolderWhenBaseModelMissing(string? baseModel)
    {
        var sourcePath = "E:/Models";
        var folderPath = "E:/Models/SomeModel";

        var result = LoraHelperTreeBuilder.BuildMergedSegments(sourcePath, folderPath, baseModel);

        result.Should().Equal(
            new[]
            {
                LoraHelperTreeBuilder.BaseLoraRootName,
                LoraHelperTreeBuilder.UnknownBaseModelFolderName,
                "SomeModel"
            });
    }

    [Fact]
    public void BuildMergedFolderTree_MergesAndSortsByBaseModel()
    {
        var segments = new List<IReadOnlyList<string>>
        {
            new[] { LoraHelperTreeBuilder.BaseLoraRootName, "Flux.1 D", "Alpha" },
            new[] { LoraHelperTreeBuilder.BaseLoraRootName, "Flux.1 D", "Alpha", "Nested" },
            new[] { LoraHelperTreeBuilder.BaseLoraRootName, "Flux.1 D", "Beta" },
            new[] { LoraHelperTreeBuilder.BaseLoraRootName, "SDXL", "Sample" }
        };

        var root = LoraHelperTreeBuilder.BuildMergedFolderTree(segments);

        root.Should().NotBeNull();
        root!.Name.Should().Be(LoraHelperTreeBuilder.BaseLoraRootName);
        root.ModelCount.Should().Be(4);
        root.Children.Select(c => c.Name).Should().Equal("Flux.1 D", "SDXL");

        var fluxNode = root.Children[0];
        fluxNode.ModelCount.Should().Be(3);
        fluxNode.Children.Select(c => c.Name).Should().Equal("Alpha", "Beta");
        fluxNode.Children[0].ModelCount.Should().Be(2);
        fluxNode.Children[0].Children.Should().ContainSingle()
            .Which.Name.Should().Be("Nested");
        fluxNode.Children[0].Children[0].ModelCount.Should().Be(1);

        var sdxlNode = root.Children[1];
        sdxlNode.ModelCount.Should().Be(1);
        sdxlNode.Children.Should().ContainSingle()
            .Which.Name.Should().Be("Sample");
    }
}
