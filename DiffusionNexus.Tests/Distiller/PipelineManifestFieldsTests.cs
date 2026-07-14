using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiffusionNexus.UI.Models.Pipelines;
using FluentAssertions;

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
    public void Distiller_manifest_json_parses_as_utility_without_models()
    {
        // Deserialize the REAL manifest file (copied to the test output) with the same options the
        // provider uses. This avoids Avalonia's AssetLoader so the test process stays Avalonia-free
        // (a global Avalonia init destabilizes the whole test host).
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "batch-metadata-distiller.json");
        File.Exists(path).Should().BeTrue($"manifest should be copied to the test output at {path}");

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        var m = JsonSerializer.Deserialize<PipelineManifest>(File.ReadAllText(path), options);

        m.Should().NotBeNull();
        m!.Id.Should().Be("batch-metadata-distiller");
        m.Category.Should().Be("Utilities");
        m.RequiresModels.Should().BeFalse();
        m.ShowInGallery.Should().BeTrue();
    }
}
