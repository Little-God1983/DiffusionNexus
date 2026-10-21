using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using SkiaSharp;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for upscaling images using the 4x-UltraSharp ONNX model.
/// Uses tile-based processing to handle large images within memory constraints.
/// Session lifecycle (DirectML/CPU selection, GPU demotion, disposal) lives in
/// <see cref="OnnxSessionHost"/>; this class owns only the tiling and
/// pre/post-processing.
/// </summary>
public sealed class ImageUpscalingService : IImageUpscalingService
{
    // Model parameters for 4x-UltraSharp (ESRGAN architecture)
    private const int ScaleFactor = 4;
    private const int TileSize = 192; // Tile size for processing (larger uses more VRAM)
    private const int TilePadding = 32; // Padding around tile (context) to avoid edge artifacts
    private const int TileParseSize = TileSize - 2 * TilePadding; // The actual valid content size per tile

    private readonly OnnxModelManager _modelManager;
    private readonly OnnxSessionHost _host = new("4x-UltraSharp");
    private string? _inputName;
    private string? _outputName;

    /// <summary>
    /// Creates a new ImageUpscalingService.
    /// </summary>
    public ImageUpscalingService() : this(new OnnxModelManager()) { }

    /// <summary>
    /// Creates a new ImageUpscalingService with a custom model manager.
    /// </summary>
    /// <param name="modelManager">The model manager to use.</param>
    public ImageUpscalingService(OnnxModelManager modelManager)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
    }

    /// <inheritdoc />
    public bool IsGpuAvailable => _host.IsGpuAvailable;

    /// <inheritdoc />
    public bool IsProcessing => _host.IsProcessing;

    /// <inheritdoc />
    public ModelStatus GetModelStatus() => _modelManager.GetUltraSharp4xStatus();

    /// <inheritdoc />
    public string GetModelPath() => _modelManager.UltraSharp4xModelPath;

    /// <inheritdoc />
    public Task<bool> DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return _modelManager.DownloadUltraSharp4xModelAsync(progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_host.IsInitialized)
            return true;

        var status = GetModelStatus();
        if (status != ModelStatus.Ready)
        {
            Log.Warning("Cannot initialize ImageUpscalingService: model status is {Status}", status);
            return false;
        }

        return await _host.InitializeAsync(_modelManager.UltraSharp4xModelPath, DiscoverTensorNames, cancellationToken);
    }

    /// <summary>
    /// Discovers the actual input and output tensor names from the model metadata.
    /// </summary>
    private void DiscoverTensorNames(InferenceSession session)
    {
        _inputName = session.InputMetadata.Keys.FirstOrDefault();
        _outputName = session.OutputMetadata.Keys.FirstOrDefault();

        Log.Debug("4x-UltraSharp model input name: {InputName}, output name: {OutputName}",
            _inputName, _outputName);

        if (string.IsNullOrEmpty(_inputName))
        {
            throw new InvalidOperationException("Could not determine input tensor name from model metadata");
        }

        if (string.IsNullOrEmpty(_outputName))
        {
            throw new InvalidOperationException("Could not determine output tensor name from model metadata");
        }
    }

    /// <inheritdoc />
    public async Task<ImageUpscalingResult> UpscaleImageAsync(
        byte[] imageData,
        int width,
        int height,
        float targetScale,
        IProgress<UpscalingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        if (width <= 0 || height <= 0)
            return ImageUpscalingResult.Failed("Invalid image dimensions");

        if (targetScale < 1.1f || targetScale > 4.0f)
            return ImageUpscalingResult.Failed("Target scale must be between 1.1 and 4.0");

        if (_host.IsDisposed)
            return ImageUpscalingResult.Failed("Service is disposed");

        if (!_host.TryBeginProcessing())
            return ImageUpscalingResult.Failed("Service is already processing an image");

        try
        {
            if (!await InitializeAsync(cancellationToken))
                return ImageUpscalingResult.Failed("Failed to initialize ONNX session. Please ensure the model is downloaded.");

            try
            {
                return await Task.Run(() =>
                    ProcessImage(imageData, width, height, targetScale, progress, cancellationToken),
                    cancellationToken);
            }
            catch (OnnxRuntimeException ex) when (_host.CanRetryOnCpu)
            {
                Log.Warning(ex, "GPU inference failed. Disabling GPU and retrying on CPU.");

                await _host.DemoteToCpuAsync(cancellationToken);

                // Retry initialization (will force CPU)
                if (await InitializeAsync(cancellationToken))
                {
                    try
                    {
                        Log.Information("Retrying upscaling on CPU...");
                        return await Task.Run(() =>
                            ProcessImage(imageData, width, height, targetScale, progress, cancellationToken),
                            cancellationToken);
                    }
                    catch (Exception retryEx)
                    {
                        Log.Error(retryEx, "Retry on CPU failed");
                        return ImageUpscalingResult.Failed($"Upscaling failed (CPU retry): {retryEx.Message}");
                    }
                }

                return ImageUpscalingResult.Failed($"Upscaling failed (GPU Error: {ex.Message})");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Image upscaling failed");
                return ImageUpscalingResult.Failed($"Upscaling failed: {ex.Message}");
            }
        }
        finally
        {
            _host.EndProcessing();
        }
    }

    private ImageUpscalingResult ProcessImage(
        byte[] imageData,
        int width,
        int height,
        float targetScale,
        IProgress<UpscalingProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Step 1: Load image
        progress?.Report(new UpscalingProgress(UpscalingPhase.Preparing, "Loading image...", 0));
        cancellationToken.ThrowIfCancellationRequested();

        using var originalImage = SkiaPixels.FromRgbaBytes(imageData.AsSpan(0, width * height * 4), width, height);

        // Step 2: Process through 4x upscaling model using tiles
        progress?.Report(new UpscalingProgress(UpscalingPhase.ProcessingTiles, "Generating AI details...", 5));

        using var upscaled4x = ProcessTiles(originalImage, progress, cancellationToken);

        // Step 3: Resize to target scale if needed
        var targetWidth = (int)Math.Round(width * targetScale);
        var targetHeight = (int)Math.Round(height * targetScale);

        SKBitmap finalImage;
        if (Math.Abs(targetScale - 4.0f) < 0.001f)
        {
            // Target is 4x, no additional resize needed
            progress?.Report(new UpscalingProgress(UpscalingPhase.Finalizing, "Finalizing...", 95));
            finalImage = upscaled4x.Copy();
        }
        else
        {
            // Downscale from 4x to target. Skia has no Lanczos3; SkiaPixels.Sampling is
            // Catmull-Rom (the same cubic ImageSharp's default resampler used).
            progress?.Report(new UpscalingProgress(
                UpscalingPhase.ResizingToTarget,
                $"Resizing to {targetScale:F1}x ({targetWidth}x{targetHeight})...",
                90));

            finalImage = SkiaPixels.ResizeTo(upscaled4x, targetWidth, targetHeight);
        }

        // Step 4: Encode to PNG
        progress?.Report(new UpscalingProgress(UpscalingPhase.Finalizing, "Encoding result...", 98));
        cancellationToken.ThrowIfCancellationRequested();

        byte[] png;
        using (finalImage)
        {
            png = SkiaPixels.EncodePng(finalImage);
        }

        progress?.Report(new UpscalingProgress(UpscalingPhase.Finalizing, "Complete!", 100));

        return ImageUpscalingResult.Succeeded(
            png,
            targetWidth,
            targetHeight);
    }

    /// <summary>
    /// Processes the image through the model using overlapping tiles.
    /// </summary>
    private SKBitmap ProcessTiles(
        SKBitmap input,
        IProgress<UpscalingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var inputWidth = input.Width;
        var inputHeight = input.Height;
        var outputWidth = inputWidth * ScaleFactor;
        var outputHeight = inputHeight * ScaleFactor;

        // Calculate tile grid using ParseSize (stride)
        var tilesX = (int)Math.Ceiling((double)inputWidth / TileParseSize);
        var tilesY = (int)Math.Ceiling((double)inputHeight / TileParseSize);
        var totalTiles = tilesX * tilesY;

        Log.Information("Upscaling {Width}x{Height} -> {OutWidth}x{OutHeight} using {TileCount} tiles (Padding: {Padding})",
            inputWidth, inputHeight, outputWidth, outputHeight, totalTiles, TilePadding);

        // Create output image
        var output = NewRgba(outputWidth, outputHeight);

        var tilesProcessed = 0;

        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Determine the input window coordinates (center of the tile logic)
                var inputStartX = tx * TileParseSize;
                var inputStartY = ty * TileParseSize;

                // Determine the tile extraction coordinates (including padding context)
                // These can be negative (handled by ExtractTile via clamping)
                var tileX = inputStartX - TilePadding;
                var tileY = inputStartY - TilePadding;

                // Extract tile with context
                using var tile = ExtractTile(input, tileX, tileY);

                // Process tile through model
                using var upscaledTile = ProcessSingleTile(tile);

                // Copy valid region to output
                // We discard 'TilePadding' from all sides of the upscaled result to remove edge artifacts
                var destX = inputStartX * ScaleFactor;
                var destY = inputStartY * ScaleFactor;

                var srcCropX = TilePadding * ScaleFactor;
                var srcCropY = TilePadding * ScaleFactor;

                var validWidth = TileParseSize * ScaleFactor;
                var validHeight = TileParseSize * ScaleFactor;

                CopyTileToOutput(output, upscaledTile, destX, destY, srcCropX, srcCropY, validWidth, validHeight);

                tilesProcessed++;
                var progressPct = 5 + (int)(85.0 * tilesProcessed / totalTiles);
                progress?.Report(new UpscalingProgress(
                    UpscalingPhase.ProcessingTiles,
                    $"Processing tile {tilesProcessed}/{totalTiles}...",
                    progressPct));
            }
        }

        return output;
    }

    /// <summary>
    /// Extracts a tile from the input image, padding with edge replication if out of bounds.
    /// </summary>
    private static SKBitmap ExtractTile(SKBitmap source, int x, int y)
    {
        var tile = NewRgba(TileSize, TileSize);

        for (var row = 0; row < TileSize; row++)
        {
            // Clamp Y to valid source image range (Edge Replication)
            var srcY = Math.Clamp(y + row, 0, source.Height - 1);
            var srcRow = Row(source, srcY);
            var dstRow = Row(tile, row);

            for (var col = 0; col < TileSize; col++)
            {
                // Clamp X to valid source image range
                var srcX = Math.Clamp(x + col, 0, source.Width - 1);
                srcRow.Slice(srcX * 4, 4).CopyTo(dstRow.Slice(col * 4, 4));
            }
        }

        return tile;
    }

    /// <summary>A new unpremultiplied RGBA8888 bitmap, zero-filled like ImageSharp's <c>new Image&lt;Rgba32&gt;</c>.</summary>
    private static SKBitmap NewRgba(int width, int height)
        => new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));

    /// <summary>The RGBA bytes of row <paramref name="y"/> (4 bytes per pixel, no stride padding).</summary>
    private static Span<byte> Row(SKBitmap bitmap, int y)
        => bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes, bitmap.Width * 4);

    /// <summary>
    /// Processes a single tile through the ONNX model. Each tile is one
    /// inference inside the host's Run scope, so disposal can interleave
    /// between tiles (yielding a managed failure) but never under one.
    /// </summary>
    private SKBitmap ProcessSingleTile(SKBitmap tile)
    {
        // Create input tensor [1, 3, H, W] normalized to 0-1
        var tensor = new DenseTensor<float>([1, 3, TileSize, TileSize]);

        for (var y = 0; y < TileSize; y++)
        {
            var row = Row(tile, y);
            for (var x = 0; x < TileSize; x++)
            {
                var p = x * 4;
                tensor[0, 0, y, x] = row[p] / 255.0f;     // R
                tensor[0, 1, y, x] = row[p + 1] / 255.0f; // G
                tensor[0, 2, y, x] = row[p + 2] / 255.0f; // B
            }
        }

        // Run inference using discovered input name
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName!, tensor)
        };

        return _host.Run(inputs, results =>
        {
            var outputTensor = results.First().AsTensor<float>();

            // Convert output tensor to image
            var outputSize = TileSize * ScaleFactor;
            var output = NewRgba(outputSize, outputSize);
            try
            {
                for (var y = 0; y < outputSize; y++)
                {
                    var row = Row(output, y);
                    for (var x = 0; x < outputSize; x++)
                    {
                        var p = x * 4;
                        row[p] = (byte)Math.Clamp(outputTensor[0, 0, y, x] * 255.0f, 0, 255);
                        row[p + 1] = (byte)Math.Clamp(outputTensor[0, 1, y, x] * 255.0f, 0, 255);
                        row[p + 2] = (byte)Math.Clamp(outputTensor[0, 2, y, x] * 255.0f, 0, 255);
                        row[p + 3] = 255;
                    }
                }
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        });
    }

    /// <summary>
    /// Copies the valid central region of an upscaled tile to the output image.
    /// </summary>
    private static void CopyTileToOutput(
        SKBitmap output,
        SKBitmap tile,
        int destX,
        int destY,
        int srcX,
        int srcY,
        int width,
        int height)
    {
        for (var row = 0; row < height; row++)
        {
            var dy = destY + row;
            if (dy >= output.Height) break; // Clip bottom edge

            var sy = srcY + row;
            var outputRow = Row(output, dy);
            var tileRow = Row(tile, sy);

            var copyWidth = Math.Min(width, output.Width - destX); // Clip right edge
            if (copyWidth <= 0) continue;

            tileRow.Slice(srcX * 4, copyWidth * 4).CopyTo(outputRow.Slice(destX * 4, copyWidth * 4));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _host.Dispose();
}
