using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.LoraSort.Service.Classes;
using System.Collections.Generic;
using System.IO;
using Xunit;

public class LoraCardViewModelTests
{
    [Fact]
    public void DiffusionProperties_ReturnExpectedValues()
    {
        var model = new ModelClass
        {
            ModelName = "test",
            DiffusionBaseModel = "SD15",
            ModelType = DiffusionTypes.LORA,
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var vm = new LoraCardViewModel { Model = model };

        Assert.Single(vm.DiffusionTypes);
        Assert.Contains("LORA", vm.DiffusionTypes);
        Assert.Equal("SD15", vm.DiffusionBaseModel);
    }

    [Fact]
    public void DiffusionProperties_HandleNullModel()
    {
        var vm = new LoraCardViewModel { Model = null };

        Assert.Empty(vm.DiffusionTypes);
        Assert.Equal(string.Empty, vm.DiffusionBaseModel);
    }
}
