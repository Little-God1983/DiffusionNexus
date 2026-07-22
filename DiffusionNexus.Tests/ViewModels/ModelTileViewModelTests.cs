using System.Reflection;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;
using SkiaSharp;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// #438 regression + seam tests for <see cref="ModelTileViewModel"/>. The tile is now
/// constructed with an injected <see cref="ModelTileDependencies"/> bundle instead of
/// reaching into the <c>App.Services</c> static locator, so it can be exercised with
/// mocks/fakes. These tests also guard the two historical production incident sites the
/// issue calls out:
/// <list type="bullet">
///   <item>socket exhaustion from a fresh <c>HttpClient</c> per thumbnail download —
///   the tile must keep a single shared static client;</item>
///   <item>OOM / DB bloat from oversized thumbnail BLOBs — <c>ResizeIfOversized</c> must
///   cap images that exceed the byte limit.</item>
/// </list>
/// No Avalonia platform is initialised (which would deadlock the suite): the clipboard,
/// scheduler and dialog boundaries are all faked, and the thumbnail-resize guard runs on
/// SkiaSharp alone.
/// </summary>
public class ModelTileViewModelTests
{
    /// <summary>Records the text handed to the clipboard seam.</summary>
    private sealed class RecordingClipboard : IClipboardService
    {
        public List<string> Copied { get; } = [];

        public Task SetTextAsync(string text)
        {
            Copied.Add(text);
            return Task.CompletedTask;
        }
    }

    private static Model CreateLocalModel(string fileName, bool withCivitaiIds = false)
    {
        var model = new Model
        {
            Id = 7,
            Name = "Local Only LoRA",
            Type = ModelType.LORA,
            CivitaiId = withCivitaiIds ? 555 : null,
            CivitaiModelPageId = withCivitaiIds ? 555 : null,
        };

        var version = new ModelVersion
        {
            Id = 700,
            Name = "v1.0",
            BaseModelRaw = "Flux.1 D",
            CivitaiId = withCivitaiIds ? 5550 : null,
            Model = model,
        };
        version.Files.Add(new ModelFile { Id = 7000, FileName = fileName, IsPrimary = true, ModelVersion = version });
        model.Versions.Add(version);
        return model;
    }

    [Fact]
    public void FromModelWithADependencyBundleConstructsWithoutTheLocator()
    {
        var deps = new ModelTileDependencies(
            Logger: new Mock<IUnifiedLogger>().Object,
            Clipboard: new RecordingClipboard());

        var act = () => ModelTileViewModel.FromModel(CreateLocalModel("a.safetensors"), deps);

        act.Should().NotThrow("the tile must be constructible with an injected bundle, not App.Services");
    }

    [Fact]
    public void OpenOnCivitaiWithNoLinkWarnsThroughTheInjectedLogger()
    {
        // A local-only model (no Civitai id anywhere) has no page to open, so the command
        // logs a warning. That it reaches the *injected* logger proves the locator is gone.
        var logger = new Mock<IUnifiedLogger>();
        var tile = ModelTileViewModel.FromModel(
            CreateLocalModel("a.safetensors", withCivitaiIds: false),
            new ModelTileDependencies(Logger: logger.Object));

        tile.OpenOnCivitaiCommand.Execute(null);

        logger.Verify(
            l => l.Warn(It.IsAny<LogCategory>(), "OpenOnCivitai", It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once());
    }

    [Fact]
    public async Task CopyFileNameRoutesThroughTheInjectedClipboard()
    {
        var clipboard = new RecordingClipboard();
        var tile = ModelTileViewModel.FromModel(
            CreateLocalModel("my_cool_lora.safetensors"),
            new ModelTileDependencies(Clipboard: clipboard));

        await tile.CopyFileNameCommand.ExecuteAsync(null);

        clipboard.Copied.Should().ContainSingle().Which.Should().Be("my_cool_lora");
    }

    [Fact]
    public void ThumbnailDownloadsShareASingleStaticHttpClient()
    {
        // Socket-exhaustion incident: a `new HttpClient()` per download accumulated
        // TIME_WAIT sockets until OOM. The fix is one shared static client — assert the
        // field is still there, static and readonly (single shared instance).
        var field = typeof(ModelTileViewModel).GetField(
            "s_thumbnailClient", BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull("thumbnail downloads must reuse one shared HttpClient");
        field!.IsInitOnly.Should().BeTrue("the shared client must be readonly so it can't be swapped per-call");
        field.FieldType.Should().Be<HttpClient>();
        field.GetValue(null).Should().NotBeNull();
    }

    [Fact]
    public void ResizeIfOversizedCapsAThumbnailThatExceedsTheByteLimit()
    {
        // OOM / DB-bloat incident: Civitai's CDN sometimes ignores width= and returns a
        // full-resolution image (up to 25 MB seen in prod). ResizeIfOversized must shrink
        // anything over the ~1 MB limit before it is persisted / decoded.
        var oversized = CreateNoisyPng(1200, 1200);
        oversized.Length.Should().BeGreaterThan(1_048_576, "the test input must exceed the resize threshold");

        var method = typeof(ModelTileViewModel).GetMethod(
            "ResizeIfOversized", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = ((byte[] Data, string MimeType))method.Invoke(
            null, [oversized, "image/png", null, "IncidentTest"])!;

        result.Data.Length.Should().BeLessThan(oversized.Length, "the oversized thumbnail must be shrunk");
        result.Data.Length.Should().BeLessThanOrEqualTo(1_048_576, "the resized thumbnail must fit under the cap");
    }

    /// <summary>
    /// Builds a PNG of random noise so it does not compress away — a reliable way to get a
    /// decodable image comfortably over the 1 MB resize threshold without any test asset.
    /// </summary>
    private static byte[] CreateNoisyPng(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);

        var pixels = new byte[info.BytesSize];
        new Random(42).NextBytes(pixels);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
