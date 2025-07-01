using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using DiffusionNexus.LoraSort.Service.Classes;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views;
using DiffusionNexus.UI.Classes;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.VisualTree;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

public class LoraHelperFilterTests
{
    [Fact]
    public void ToggleCommandAddsAndRemovesModel()
    {
        var vm = new LoraHelperViewModel(new FakeService());
        vm.ToggleModelCommand.Execute("SDXL");
        Assert.Contains("SDXL", vm.SelectedDiffusionModels);
        vm.ToggleModelCommand.Execute("SDXL");
        Assert.DoesNotContain("SDXL", vm.SelectedDiffusionModels);
    }

    [Fact]
    public void FilteringBySelectedModelsReturnsExpectedCards()
    {
        var vm = new LoraHelperViewModel(new FakeService());
        var card1 = new LoraCardViewModel { Model = new ModelClass { ModelName = "a", DiffusionBaseModel = "SD 1.5", ModelType = DiffusionTypes.LORA, AssociatedFilesInfo = new List<FileInfo>() } };
        var card2 = new LoraCardViewModel { Model = new ModelClass { ModelName = "b", DiffusionBaseModel = "SDXL", ModelType = DiffusionTypes.LORA, AssociatedFilesInfo = new List<FileInfo>() } };
        var field = typeof(LoraHelperViewModel).GetField("_allCards", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(vm, new List<LoraCardViewModel> { card1, card2 });
        var method = typeof(LoraHelperViewModel).GetMethod("FilterCards", BindingFlags.NonPublic | BindingFlags.Instance);
        var result = (List<LoraCardViewModel>)method!.Invoke(vm, new object?[] { null, null })!;
        Assert.Equal(2, result.Count);
        vm.SelectedDiffusionModels.Add("SDXL");
        result = (List<LoraCardViewModel>)method.Invoke(vm, new object?[] { null, null })!;
        Assert.Single(result);
        Assert.Equal("SDXL", result[0].DiffusionBaseModel);
    }

    [Fact]
    public void FlyoutContainsButtonsForEachModel()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DiffusionNexus.UI.App));
        session.Dispatch(() =>
        {
            var vm = new LoraHelperViewModel(new FakeService());
            var view = new LoraHelperView { DataContext = vm };
            view.ApplyTemplate();
            view.Measure(new Size(300,300));
            view.Arrange(new Rect(0,0,300,300));
            var button = view.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == "⚙");
            var flyout = button.Flyout as Flyout;
            Assert.NotNull(flyout);
            var itemsControl = (flyout!.Content as ItemsControl)!;
            Assert.Equal(vm.DiffusionModels.Count, itemsControl.Items.Cast<object>().Count());
        }, System.Threading.CancellationToken.None);
    }

    private class FakeService : ISettingsService
    {
        public Task<SettingsModel> LoadAsync() => Task.FromResult(new SettingsModel());
        public Task SaveAsync(SettingsModel settings) => Task.CompletedTask;
    }
}
