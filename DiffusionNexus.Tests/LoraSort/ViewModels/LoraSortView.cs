using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Views;
using FluentAssertions;
using System.Collections.ObjectModel;
using Xunit;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.Tests.LoraSort.Views;

public class LoraSortViewCustomTagTests : System.IDisposable
{
    private readonly string _mappingFilePath;

    public LoraSortViewCustomTagTests()
    {
        _mappingFilePath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "mappings.xml");
        DeleteMappingFile();
    }

    private void DeleteMappingFile()
    {
        if (File.Exists(_mappingFilePath))
            File.Delete(_mappingFilePath);
    }

    [Fact]
    public void CustomTag_CustomMappingsOff_UsesCategoryFolder()
    {
        var model = new ModelClass
        {
            DiffusionBaseModel = "Base",
            CivitaiCategory = CivitaiBaseCategories.ANIMAL,
            Tags = new List<string> { "t1" }
        };
        var options = new SelectedOptions
        {
            TargetPath = "Target",
            CreateBaseFolders = true,
            UseCustomMappings = false
        };

        var path = LoraSortView.CustomTag(model, options);
        path.Should().Be(Path.Combine("Target", "Base", "ANIMAL"));
    }

    [Fact]
    public void CustomTag_FirstMappingMatches_ReturnsMappedFolder()
    {
        var mapping = new CustomTagMap { LookForTag = new List<string> { "land" }, MapToFolder = "Landscape", Priority = 1 };
        var xml = new CustomTagMapXmlService();
        xml.SaveMappings(new ObservableCollection<CustomTagMap> { mapping });

        var model = new ModelClass
        {
            DiffusionBaseModel = "Base",
            CivitaiCategory = CivitaiBaseCategories.ANIMAL,
            Tags = new List<string> { "land" }
        };
        var options = new SelectedOptions
        {
            TargetPath = "Target",
            CreateBaseFolders = true,
            UseCustomMappings = true
        };

        var path = LoraSortView.CustomTag(model, options);
        path.Should().Be(Path.Combine("Target", "Base", "Landscape"));
    }

    [Fact]
    public void CustomTag_NoMappingMatch_FallsBackToCategory()
    {
        var mapping = new CustomTagMap { LookForTag = new List<string> { "other" }, MapToFolder = "Other", Priority = 1 };
        var xml = new CustomTagMapXmlService();
        xml.SaveMappings(new ObservableCollection<CustomTagMap> { mapping });

        var model = new ModelClass
        {
            DiffusionBaseModel = "Base",
            CivitaiCategory = CivitaiBaseCategories.ANIMAL,
            Tags = new List<string> { "nomatch" }
        };
        var options = new SelectedOptions
        {
            TargetPath = "Target",
            CreateBaseFolders = true,
            UseCustomMappings = true
        };

        var path = LoraSortView.CustomTag(model, options);
        path.Should().Be(Path.Combine("Target", "Base", "ANIMAL"));
    }

    [Fact]
    public void CustomTag_TagMatching_IgnoresCaseAndSpacing()
    {
        var mapping = new CustomTagMap { LookForTag = new List<string> { "Custom_Tag" }, MapToFolder = "Case", Priority = 1 };
        var xml = new CustomTagMapXmlService();
        xml.SaveMappings(new ObservableCollection<CustomTagMap> { mapping });

        var model = new ModelClass
        {
            DiffusionBaseModel = "Base",
            CivitaiCategory = CivitaiBaseCategories.ANIMAL,
            Tags = new List<string> { " custom_tag " }
        };
        var options = new SelectedOptions
        {
            TargetPath = "Target",
            CreateBaseFolders = true,
            UseCustomMappings = true
        };

        var path = LoraSortView.CustomTag(model, options);
        path.Should().Be(Path.Combine("Target", "Base", "Case"));
    }

    [Fact]
    public void CustomTag_MultipleMappings_HighestPriorityWins()
    {
        var map1 = new CustomTagMap { LookForTag = new List<string> { "a" }, MapToFolder = "First", Priority = 2 };
        var map2 = new CustomTagMap { LookForTag = new List<string> { "a" }, MapToFolder = "Second", Priority = 1 };
        var xml = new CustomTagMapXmlService();
        xml.SaveMappings(new ObservableCollection<CustomTagMap> { map1, map2 });

        var model = new ModelClass
        {
            DiffusionBaseModel = "Base",
            CivitaiCategory = CivitaiBaseCategories.ANIMAL,
            Tags = new List<string> { "a" }
        };
        var options = new SelectedOptions
        {
            TargetPath = "Target",
            CreateBaseFolders = true,
            UseCustomMappings = true
        };

        var path = LoraSortView.CustomTag(model, options);
        path.Should().Be(Path.Combine("Target", "Base", "Second"));
    }

    public void Dispose()
    {
        DeleteMappingFile();
    }
}