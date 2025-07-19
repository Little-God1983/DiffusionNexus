using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views;
using DiffusionNexus.Service.Services;
using System.Net.Http;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.LoraSort.UI;

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

            var mock = new Mock<ISettingsService>();
            mock.Setup(s => s.LoadAsync()).ReturnsAsync(new SettingsModel());
            mock.Setup(s => s.SaveAsync(It.IsAny<SettingsModel>())).Returns(Task.CompletedTask);
            var vm = new LoraHelperViewModel(mock.Object, new LoraMetadataDownloadService(new CivitaiApiClient(new HttpClient())));
            vm.Cards.Add(cardVm);
            var view = new LoraHelperView { DataContext = vm };
            view.ApplyTemplate();
            view.Measure(new Size(300, 300));
            view.Arrange(new Rect(0,0,300,300));

            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            texts.Should().Contain("LORA");
            texts.Should().Contain("SD15");
            texts.Should().Contain("card");
        }, System.Threading.CancellationToken.None);
    }
}
