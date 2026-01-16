using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views.Dialogs;
using FluentAssertions;

namespace DiffusionNexus.IntegrationTests;

public class ReplaceDialogTests : IClassFixture<TestAppHost>
{
    private readonly TestAppHost _host;

    public ReplaceDialogTests(TestAppHost host)
    {
        _host = host;
    }

    [AvaloniaFact]
    public async Task ReplaceButton_DisablesUntilNewFileIsSet()
    {
        var viewModel = new ReplaceImageDialogViewModel();
        var dialog = new ReplaceDialog
        {
            DataContext = viewModel
        };

        dialog.Show();

        var replaceButton = dialog.GetVisualDescendants()
            .OfType<Button>()
            .First(button => string.Equals(button.Content?.ToString(), "Replace", StringComparison.Ordinal));

        replaceButton.IsEnabled.Should().BeFalse("no new file has been selected");

        var tempImagePath = CreateTempPng(_host.RootPath);
        await viewModel.SetNewFileAsync(tempImagePath);

        await Dispatcher.UIThread.InvokeAsync(() => { });

        replaceButton.IsEnabled.Should().BeTrue("a new file has been selected");

        dialog.Close();
    }

    private static string CreateTempPng(string rootPath)
    {
        var imagePath = Path.Combine(rootPath, $"replace-test-{Guid.NewGuid():N}.png");
        var imageBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");
        File.WriteAllBytes(imagePath, imageBytes);
        return imagePath;
    }
}
