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

    [Fact]
    public void LoadImages_AnimatedImage_SaysUpFrontThatItCannotBeSaved()
    {
        // Animated content under a writable extension: decided by the file, not the name.
        var path = Path.Combine(_dir, "anim.png");
        File.WriteAllBytes(path, TwoFrameGif());
        var vm = new ColorFixerViewModel();

        vm.LoadImages([Item(path)]);

        var item = vm.Images.Single();
        item.CanSave.Should().BeFalse();
        item.FixError.Should().Contain("animated");
    }

    [Fact]
    public async Task AutoSet_AfterAnEarlierFailure_ClearsTheStaleError()
    {
        var path = Path.Combine(_dir, "tinted.png");
        var rgba = new byte[16 * 16 * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 220; rgba[i + 1] = 90; rgba[i + 2] = 60; rgba[i + 3] = 255; }
        using (var bmp = DiffusionNexus.Service.Services.Imaging.SkiaPixels.FromRgbaBytes(rgba, 16, 16))
            DiffusionNexus.Service.Services.Imaging.SkiaPixels.SavePng(bmp, path);
        var vm = new ColorFixerViewModel();
        vm.LoadImages([Item(path)]);
        var item = vm.Images.Single();
        item.FixError = "Could not preview this image: the file was locked";
        vm.SelectedImage = item;

        await vm.AutoSetCommand.ExecuteAsync(null);

        item.FixError.Should().BeNull();
    }

    [Fact]
    public async Task AutoSet_FailingOnAnUnsavableImage_KeepsTheExplanationWhyItCannotBeSaved()
    {
        // A preview or Auto Set failure on a GIF must not replace "can't save .gif files" with a
        // decode error; the user would lose the reason the image is left alone.
        var path = Path.Combine(_dir, "cast.gif");
        File.WriteAllBytes(path, [9, 9, 9]);
        var vm = new ColorFixerViewModel();
        vm.LoadImages([Item(path)]);
        var item = vm.Images.Single();
        vm.SelectedImage = item;

        await vm.AutoSetCommand.ExecuteAsync(null);

        item.FixError.Should().Contain(".gif");
    }

    private static byte[] TwoFrameGif()
    {
        byte[] frame =
        [
            0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00,
            0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0x02, 0x02, 0x44, 0x01, 0x00,
        ];
        return [.. "GIF89a"u8, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, .. frame, .. frame, 0x3B];
    }

    private static ColorDistributionItemViewModel Item(string path) => new()
    {
        FileName = Path.GetFileName(path),
        FilePath = path,
        Score = 40,
        Detail = "Strong color tint detected",
    };
}
