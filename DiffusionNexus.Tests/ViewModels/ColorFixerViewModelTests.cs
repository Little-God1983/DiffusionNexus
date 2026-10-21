using DiffusionNexus.UI.ViewModels.Dialogs;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

public class ColorFixerViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dn-colorfixer-" + Guid.NewGuid().ToString("N"));

    public ColorFixerViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void LoadImages_GifImage_SaysUpFrontThatItCannotBeSaved()
    {
        var vm = new ColorFixerViewModel();

        vm.LoadImages([Item(Path.Combine(_dir, "cast.gif"))]);

        var item = vm.Images.Should().ContainSingle().Subject;
        item.CanSave.Should().BeFalse();
        item.FixError.Should().Contain(".gif");
    }

    [Fact]
    public void LoadImages_PngImage_CanBeSaved()
    {
        var vm = new ColorFixerViewModel();

        vm.LoadImages([Item(Path.Combine(_dir, "cast.png"))]);

        var item = vm.Images.Should().ContainSingle().Subject;
        item.CanSave.Should().BeTrue();
        item.FixError.Should().BeNull();
    }

    [Fact]
    public async Task FixAll_ImageThatCannotBeRead_ShowsTheErrorOnTheItemInsteadOfOnlyLoggingIt()
    {
        var path = Path.Combine(_dir, "broken.png");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var vm = new ColorFixerViewModel();
        vm.LoadImages([Item(path)]);

        await vm.FixAllCommand.ExecuteAsync(null);

        var item = vm.Images.Single();
        item.IsFixed.Should().BeFalse();
        item.FixError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FixAll_GifImage_IsLeftUntouchedWithItsReason()
    {
        var path = Path.Combine(_dir, "cast.gif");
        File.WriteAllBytes(path, [9, 9, 9]);
        var vm = new ColorFixerViewModel();
        vm.LoadImages([Item(path)]);

        await vm.FixAllCommand.ExecuteAsync(null);

        vm.Images.Single().IsFixed.Should().BeFalse();
        vm.Images.Single().FixError.Should().Contain(".gif");
        File.ReadAllBytes(path).Should().Equal(new byte[] { 9, 9, 9 });
    }

    private static ColorDistributionItemViewModel Item(string path) => new()
    {
        FileName = Path.GetFileName(path),
        FilePath = path,
        Score = 40,
        Detail = "Strong color tint detected",
    };
}
