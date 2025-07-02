using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using DiffusionNexus.LoraSort.Service.Classes;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

public class LoraCardViewUiTests
{
    [Fact]
    public void LabelsAppearWithCorrectText()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DiffusionNexus.UI.App));
        session.Dispatch(() => {
            var cardVm = new LoraCardViewModel
            {
                Model = new ModelClass
                {
                    SafeTensorFileName = "card",
                    DiffusionBaseModel = "SD15",
                    ModelType = DiffusionTypes.LORA,
                    AssociatedFilesInfo = new List<FileInfo>()
                }
            };

            var vm = new LoraHelperViewModel(new FakeService());
            vm.Cards.Add(cardVm);
            var view = new LoraHelperView { DataContext = vm };
            view.ApplyTemplate();
            view.Measure(new Size(300, 300));
            view.Arrange(new Rect(0,0,300,300));

            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("LORA", texts);
            Assert.Contains("SD15", texts);
        }, System.Threading.CancellationToken.None);
    }

    private class FakeService : ISettingsService
    {
        public Task<SettingsModel> LoadAsync() => Task.FromResult(new SettingsModel());
        public Task SaveAsync(SettingsModel settings) => Task.CompletedTask;
    }
}
