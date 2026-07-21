using System.Reflection;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;
using SkiaSharp;

namespace DiffusionNexus.Tests.ViewModels.Tabs;

/// <summary>
/// Proves the <see cref="IUiScheduler"/> seam on <see cref="BatchUpscaleTabViewModel"/>:
/// <c>LoadThumbnailAsync</c> decodes off-thread and then marshals the decoded
/// bitmap onto the UI thread via <c>InvokeAsync</c> before assigning it to the
/// item. With <see cref="ImmediateUiScheduler"/> that invoke runs inline, so the
/// assignment is observable synchronously — a real Dispatcher would never pump it
/// in a headless test.
/// <para>
/// This is the only ViewModel whose seam sits behind a real Avalonia bitmap
/// decode, hence the <see cref="HeadlessAppFixture"/> (needed purely so the
/// decoder can construct a <c>Bitmap</c>).
/// </para>
/// </summary>
[Collection("Headless Avalonia")]
public class BatchUpscaleTabViewModelSchedulerTests
{
    private static readonly MethodInfo LoadThumbnailMethod =
        typeof(BatchUpscaleTabViewModel).GetMethod(
            "LoadThumbnailAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("LoadThumbnailAsync not found.");

    private static BatchUpscaleTabViewModel CreateVm(IUiScheduler scheduler)
        => new(
            new DatasetEventAggregator(),
            new Mock<IDatasetState>().Object,
            uiScheduler: scheduler);

    private static string CreateTempPng(int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dn-upscale-thumb-{Guid.NewGuid():N}.png");
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.SeaGreen);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    private static Task InvokeLoadThumbnail(BatchUpscaleTabViewModel vm, UpscaleImageItemViewModel item, string path, bool isOriginal)
        => (Task)LoadThumbnailMethod.Invoke(vm, new object[] { item, path, isOriginal })!;

    [Fact]
    public async Task WhenTheOriginalThumbnailDecodesThenItIsAssignedThroughTheScheduler()
    {
        var png = CreateTempPng(300, 200);
        try
        {
            var scheduler = new ImmediateUiScheduler();
            var vm = CreateVm(scheduler);
            var item = new UpscaleImageItemViewModel { FileName = "img.png", OriginalPath = png };

            await InvokeLoadThumbnail(vm, item, png, isOriginal: true);

            // The decoded bitmap was marshalled onto the item through the seam.
            scheduler.InvokeCount.Should().Be(1);
            item.OriginalThumbnail.Should().NotBeNull();
            item.UpscaledThumbnail.Should().BeNull("only the original thumbnail was requested");
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Fact]
    public async Task WhenTheUpscaledThumbnailDecodesThenTheUpscaledSlotIsAssignedThroughTheScheduler()
    {
        var png = CreateTempPng(256, 256);
        try
        {
            var scheduler = new ImmediateUiScheduler();
            var vm = CreateVm(scheduler);
            var item = new UpscaleImageItemViewModel { FileName = "img.png", UpscaledPath = png };

            await InvokeLoadThumbnail(vm, item, png, isOriginal: false);

            scheduler.InvokeCount.Should().Be(1);
            item.UpscaledThumbnail.Should().NotBeNull();
            item.OriginalThumbnail.Should().BeNull("only the upscaled thumbnail was requested");
        }
        finally
        {
            File.Delete(png);
        }
    }
}
