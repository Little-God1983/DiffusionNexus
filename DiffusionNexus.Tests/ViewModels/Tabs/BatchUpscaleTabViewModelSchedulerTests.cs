using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels.Tabs;

/// <summary>
/// Proves the <see cref="IUiScheduler"/> seam on <see cref="BatchUpscaleTabViewModel"/>:
/// <c>LoadThumbnailAsync</c> decodes off-thread and then marshals the decoded bitmap
/// onto the item via <c>InvokeAsync</c>. With <see cref="ImmediateUiScheduler"/> that
/// invoke runs inline, so the assignment is observable synchronously — no Avalonia
/// dispatcher (and no global Avalonia platform init, which deadlocks the suite).
/// <para>
/// The decode itself is the other, un-fakeable boundary in front of the marshalling
/// (a real Avalonia <c>Bitmap</c> can't be constructed without a platform), so the VM
/// exposes an injectable decoder. The test feeds a sentinel bitmap through it purely
/// as an opaque non-null reference — it is never operated on, and its finalizer is
/// suppressed — which lets the marshalling be asserted without any rendering stack.
/// </para>
/// </summary>
public class BatchUpscaleTabViewModelSchedulerTests
{
    private static readonly MethodInfo LoadThumbnailMethod =
        typeof(BatchUpscaleTabViewModel).GetMethod(
            "LoadThumbnailAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("LoadThumbnailAsync not found.");

    /// <summary>
    /// A non-null <see cref="Bitmap"/> reference created without invoking a constructor
    /// (so no Avalonia platform is required) and never dereferenced. Finalizer is
    /// suppressed so GC cannot touch its uninitialized native fields.
    /// </summary>
    private static Bitmap SentinelBitmap()
    {
        var bmp = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
        GC.SuppressFinalize(bmp);
        return bmp;
    }

    private static BatchUpscaleTabViewModel CreateVm(IUiScheduler scheduler, Func<string, int, Bitmap?> decoder)
        => new(
            new DatasetEventAggregator(),
            new Mock<IDatasetState>().Object,
            uiScheduler: scheduler,
            thumbnailDecoder: decoder);

    private static Task InvokeLoadThumbnail(BatchUpscaleTabViewModel vm, UpscaleImageItemViewModel item, string path, bool isOriginal)
        => (Task)LoadThumbnailMethod.Invoke(vm, new object[] { item, path, isOriginal })!;

    [Fact]
    public async Task WhenTheOriginalThumbnailDecodesThenItIsAssignedThroughTheScheduler()
    {
        var thumb = SentinelBitmap();
        var scheduler = new ImmediateUiScheduler();
        var vm = CreateVm(scheduler, (_, _) => thumb);
        var item = new UpscaleImageItemViewModel { FileName = "img.png", OriginalPath = "img.png" };

        await InvokeLoadThumbnail(vm, item, "img.png", isOriginal: true);

        // The decoded bitmap was marshalled onto the original slot through the seam.
        scheduler.InvokeCount.Should().Be(1);
        item.OriginalThumbnail.Should().BeSameAs(thumb);
        item.UpscaledThumbnail.Should().BeNull("only the original thumbnail was requested");
    }

    [Fact]
    public async Task WhenTheUpscaledThumbnailDecodesThenTheUpscaledSlotIsAssignedThroughTheScheduler()
    {
        var thumb = SentinelBitmap();
        var scheduler = new ImmediateUiScheduler();
        var vm = CreateVm(scheduler, (_, _) => thumb);
        var item = new UpscaleImageItemViewModel { FileName = "img.png", UpscaledPath = "img.png" };

        await InvokeLoadThumbnail(vm, item, "img.png", isOriginal: false);

        scheduler.InvokeCount.Should().Be(1);
        item.UpscaledThumbnail.Should().BeSameAs(thumb);
        item.OriginalThumbnail.Should().BeNull("only the upscaled thumbnail was requested");
    }

    [Fact]
    public async Task WhenTheDecodeFailsThenNothingIsMarshalled()
    {
        var scheduler = new ImmediateUiScheduler();
        var vm = CreateVm(scheduler, (_, _) => null);
        var item = new UpscaleImageItemViewModel { FileName = "img.png", OriginalPath = "img.png" };

        await InvokeLoadThumbnail(vm, item, "img.png", isOriginal: true);

        // No bitmap -> no UI-thread work is scheduled and the slot stays empty.
        scheduler.InvokeCount.Should().Be(0);
        item.OriginalThumbnail.Should().BeNull();
    }
}
