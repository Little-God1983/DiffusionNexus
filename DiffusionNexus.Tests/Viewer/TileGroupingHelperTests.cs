using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Unit tests for <see cref="TileGroupingHelper"/>.
/// </summary>
public class TileGroupingHelperTests
{
    #region GroupModelsIntoTiles

    [Fact]
    public void WhenTwoModelsSharePageIdAndDifferentFilesThenOneTileCreated()
    {
        var models = new List<Model>
        {
            CreateModel(id: 10, name: "Cool LoRA", pageId: 123, fileName: "cool_flux.safetensors", baseModel: "Flux.1 D"),
            CreateModel(id: 11, name: "Cool LoRA", pageId: 123, fileName: "cool_sdxl.safetensors", baseModel: "SDXL 1.0"),
        };

        var tiles = TileGroupingHelper.GroupModelsIntoTiles(models);

        tiles.Should().HaveCount(1, "models with the same CivitaiModelPageId should be grouped into one tile");
        tiles[0].Versions.Should().HaveCount(2);
    }

    [Fact]
    public void WhenTwoModelsSharePageIdAndSameFileNameThenOneTileCreated()
    {
        // Scenario: same LoRA file discovered in two directories (same filename).
        // DeduplicateModels drops the weaker duplicate — it must NOT leak to Phase 2.
        var models = new List<Model>
        {
            CreateModel(id: 10, name: "Cool LoRA", pageId: 123, fileName: "cool_lora.safetensors",
                baseModel: "Flux.1 D", hasCivitaiId: true),
            CreateModel(id: 11, name: "cool_lora", pageId: 123, fileName: "cool_lora.safetensors",
                baseModel: "Flux.1 D", hasCivitaiId: false),
        };

        var tiles = TileGroupingHelper.GroupModelsIntoTiles(models);

        tiles.Should().HaveCount(1, "dropped duplicate must not escape to Phase 2 as a separate tile");
    }

    [Fact]
    public void WhenModelLacksPageIdThenGroupedByName()
    {
        var models = new List<Model>
        {
            CreateModel(id: 10, name: "Fantasy Style", pageId: null, fileName: "fantasy_sd15.safetensors", baseModel: "SD 1.5"),
            CreateModel(id: 11, name: "Fantasy Style", pageId: null, fileName: "fantasy_sdxl.safetensors", baseModel: "SDXL 1.0"),
        };

        var tiles = TileGroupingHelper.GroupModelsIntoTiles(models);

        tiles.Should().HaveCount(1, "models with the same name should be grouped when no CivitaiModelPageId");
        tiles[0].Versions.Should().HaveCount(2);
    }

    [Fact]
    public void WhenModelsHaveDifferentPageIdsThenSeparateTilesCreated()
    {
        var models = new List<Model>
        {
            CreateModel(id: 10, name: "LoRA A", pageId: 100, fileName: "lora_a.safetensors", baseModel: "Flux.1 D"),
            CreateModel(id: 11, name: "LoRA B", pageId: 200, fileName: "lora_b.safetensors", baseModel: "SDXL 1.0"),
        };

        var tiles = TileGroupingHelper.GroupModelsIntoTiles(models);

        tiles.Should().HaveCount(2, "different CivitaiModelPageId means different LoRAs");
    }

    [Fact]
    public void WhenPhase1ConsumesModelThenPhase2DoesNotDuplicate()
    {
        // Model A grouped by PageId in Phase 1.
        // Model B has no PageId but same Name — should go to Phase 2 and merge there,
        // NOT create a duplicate of Model A.
        var models = new List<Model>
        {
            CreateModel(id: 10, name: "Cool LoRA", pageId: 123, fileName: "cool_flux.safetensors", baseModel: "Flux.1 D"),
            CreateModel(id: 11, name: "Cool LoRA", pageId: null, fileName: "cool_sdxl.safetensors", baseModel: "SDXL 1.0"),
        };

        var tiles = TileGroupingHelper.GroupModelsIntoTiles(models);

        // Model A → Phase 1 tile. Model B → Phase 2 tile (separate, different PageId context).
        // These are 2 tiles because Model B's PageId is null (not grouped with Model A).
        tiles.Should().HaveCount(2, "Phase 1 groups by PageId; Model B has null PageId so it falls to Phase 2");
    }

    #endregion

    #region DeduplicateModels

    [Fact]
    public void WhenModelsHaveSameFileNameThenOnlySurvivorReturned()
    {
        var models = new List<Model>
        {
            CreateModel(id: 10, name: "LoRA", pageId: 123, fileName: "lora.safetensors", baseModel: "Flux.1 D", hasCivitaiId: true),
            CreateModel(id: 11, name: "LoRA", pageId: 123, fileName: "lora.safetensors", baseModel: "Flux.1 D", hasCivitaiId: false),
        };

        var result = TileGroupingHelper.DeduplicateModels(models);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(10, "model with CivitaiId should win");
    }

    [Fact]
    public void WhenModelsHaveDifferentFileNamesThenBothKept()
    {
        var models = new List<Model>
        {
            CreateModel(id: 10, name: "LoRA", pageId: 123, fileName: "lora_flux.safetensors", baseModel: "Flux.1 D"),
            CreateModel(id: 11, name: "LoRA", pageId: 123, fileName: "lora_sdxl.safetensors", baseModel: "SDXL 1.0"),
        };

        var result = TileGroupingHelper.DeduplicateModels(models);

        result.Should().HaveCount(2);
    }

    #endregion

    #region IsBetterModel

    [Fact]
    public void WhenCandidateHasCivitaiIdAndCurrentDoesNotThenCandidateWins()
    {
        var candidate = CreateModel(id: 1, name: "A", pageId: null, fileName: "a.safetensors", baseModel: "Flux.1 D", hasCivitaiId: true);
        var current = CreateModel(id: 2, name: "A", pageId: null, fileName: "a.safetensors", baseModel: "Flux.1 D", hasCivitaiId: false);

        TileGroupingHelper.IsBetterModel(candidate, current).Should().BeTrue();
    }

    [Fact]
    public void WhenCurrentHasCivitaiIdAndCandidateDoesNotThenCurrentWins()
    {
        var candidate = CreateModel(id: 1, name: "A", pageId: null, fileName: "a.safetensors", baseModel: "Flux.1 D", hasCivitaiId: false);
        var current = CreateModel(id: 2, name: "A", pageId: null, fileName: "a.safetensors", baseModel: "Flux.1 D", hasCivitaiId: true);

        TileGroupingHelper.IsBetterModel(candidate, current).Should().BeFalse();
    }

    #endregion

    #region Helpers

    private static Model CreateModel(
        int id,
        string name,
        int? pageId,
        string fileName,
        string baseModel,
        bool hasCivitaiId = false)
    {
        var model = new Model
        {
            Id = id,
            Name = name,
            CivitaiModelPageId = pageId,
            CivitaiId = hasCivitaiId ? id + 1000 : null,
            Type = ModelType.LORA,
        };

        var version = new ModelVersion
        {
            Id = id * 100,
            Name = $"{name} - {baseModel}",
            BaseModelRaw = baseModel,
            Model = model,
        };

        version.Files.Add(new ModelFile
        {
            Id = id * 1000,
            FileName = fileName,
            IsPrimary = true,
            ModelVersion = version,
        });

        model.Versions.Add(version);
        return model;
    }

    #endregion
}
