using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services.Pipelines;
using FluentAssertions;
using System.Linq;

namespace DiffusionNexus.Tests.Distiller;

public class PipelineManifestFieldsTests
{
    [Fact]
    public void Manifest_defaults_are_generation_and_requires_models()
    {
        var m = new PipelineManifest();
        m.Category.Should().Be("Generation");
        m.RequiresModels.Should().BeTrue();
    }

    [Fact]
    public void Distiller_manifest_loads_as_utility_without_models()
    {
        var provider = new PipelineManifestProvider();
        var m = provider.All().FirstOrDefault(x => x.Id == "batch-metadata-distiller");

        m.Should().NotBeNull();
        m!.Category.Should().Be("Utilities");
        m.RequiresModels.Should().BeFalse();
        m.ShowInGallery.Should().BeTrue();
    }
}
