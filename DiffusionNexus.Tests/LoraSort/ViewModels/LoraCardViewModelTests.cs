using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.Service.Classes;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.LoraSort.ViewModels;

public class LoraCardViewModelTests


{
    [Fact]
    public void DiffusionProperties_ReturnExpectedValues()
    {
        var model = new ModelClass
        {
            SafeTensorFileName = "test",
            DiffusionBaseModel = "SD15",
            ModelType = DiffusionTypes.LORA,
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var vm = new LoraCardViewModel();
        vm.InitializeVariants(new[]
        {
            new ModelVariantViewModel(model, LoraVariantClassifier.DefaultVariantLabel)
        });

        vm.DiffusionTypes.Should().ContainSingle();
        vm.DiffusionTypes.Should().Contain("LORA");
        vm.DiffusionBaseModel.Should().Be("SD15");
    }

    [Fact]
    public void DiffusionProperties_HandleNullModel()
    {
        var vm = new LoraCardViewModel();

        vm.DiffusionTypes.Should().BeEmpty();
        vm.DiffusionBaseModel.Should().BeEmpty();
    }
}
