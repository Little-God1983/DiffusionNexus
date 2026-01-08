using DiffusionNexus.Domain.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for upscaling images using the 4x-UltraSharp ONNX model.
/// Performs inference locally using GPU acceleration when available, with CPU fallback.
/// Uses tile-based processing to handle large images within memory constraints.
/// </summary>
public sealed class ImageUpscalingService : IImageUpscalingService
{
    // Model parameters for 4x-UltraSharp (ESRGAN architecture)
    private const int ScaleFactor = 4;
    private const int TileSize = 192; // Tile size for processing (larger uses more VRAM)
    private const int TileOverlap = 16; // Overlap between tiles to avoid seams
    private const string InputName = "input";
    private const string OutputName = "output";

    private readonly OnnxModelManager _modelManager;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private InferenceSession? _session;
    private bool _isGpuAvailable;
    private bool _isProcessing;
    private bool _disposed;
    private bool _disableGpu;

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
    public bool IsGpuAvailable => _isGpuAvailable;

    /// <inheritdoc />
    public bool IsProcessing => _isProcessing;

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
        if (_session is not null)
            return true;

        var status = GetModelStatus();
        if (status != ModelStatus.Ready)
        {
            Log.Warning("Cannot initialize ImageUpscalingService: model status is {Status}", status);
            return false;
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null)
                return true;

            _session = await Task.Run(() => CreateSession(), cancellationToken);
            return _session is not null;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private InferenceSession? CreateSession()
    {
        var modelPath = _modelManager.UltraSharp4xModelPath;
        if (!File.Exists(modelPath))
        {
            Log.Error("4x-UltraSharp model file not found: {Path}", modelPath);
            return null;
        }

        // Try DirectML first (only if not disabled)
        if (!_disableGpu)
        {
            try
            {
                var dmlOptions = new SessionOptions();
                dmlOptions.AppendExecutionProvider_DML(0);
                dmlOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                var session = new InferenceSession(modelPath, dmlOptions);
                _isGpuAvailable = true;
                Log.Information("4x-UltraSharp ONNX session created with GPU (DirectML) acceleration");
                return session;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DirectML not available or failed to initialize, falling back to CPU");
            }
        }

        // Fall back to CPU
        try
        {
            var cpuOptions = new SessionOptions();
            cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            cpuOptions.EnableMemoryPattern = true;
            cpuOptions.EnableCpuMemArena = true;
            cpuOptions.IntraOpNumThreads = Environment.ProcessorCount;

            var session = new InferenceSession(modelPath, cpuOptions);
            _isGpuAvailable = false;
            Log.Information("4x-UltraSharp ONNX session created with CPU execution ({Threads} threads)", 
                Environment.ProcessorCount);
            return session;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create ONNX session for upscaling");
            return null;
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

        if (_isProcessing)
            return ImageUpscalingResult.Failed("Service is already processing an image");

        // Ensure session is initialized
        if (!await InitializeAsync(cancellationToken))
            return ImageUpscalingResult.Failed("Failed to initialize ONNX session. Please ensure the model is downloaded.");

        _isProcessing = true;
        try
        {
            return await Task.Run(() => 
                ProcessImage(imageData, width, height, targetScale, progress, cancellationToken), 
                cancellationToken);
        }
        catch (OnnxRuntimeException ex) when (_isGpuAvailable && !_disableGpu)
        {
            Log.Warning(ex, "GPU inference failed. Disabling GPU and retrying on CPU.");
            
            // Reset session
            await _sessionLock.WaitAsync(cancellationToken);
            try
            {
                _session?.Dispose();
                _session = null;
                _disableGpu = true;
                _isGpuAvailable = false;
            }
            finally
            {
                _sessionLock.Release();
            }

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
        finally
        {
            _isProcessing = false;
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
        try
        {
            // Step 1: Load image
            progress?.Report(new UpscalingProgress(UpscalingPhase.Preparing, "Loading image...", 0));
            cancellationToken.ThrowIfCancellationRequested();

            using var originalImage = Image.LoadPixelData<Rgba32>(imageData, width, height);

            // Step 2: Process through 4x upscaling model using tiles
            progress?.Report(new UpscalingProgress(UpscalingPhase.ProcessingTiles, "Generating AI details...", 5));
            
            using var upscaled4x = ProcessTiles(originalImage, progress, cancellationToken);

            // Step 3: Resize to target scale if needed
            var targetWidth = (int)Math.Round(width * targetScale);
            var targetHeight = (int)Math.Round(height * targetScale);

            Image<Rgba32> finalImage;
            if (Math.Abs(targetScale - 4.0f) < 0.001f)
            {
                // Target is 4x, no additional resize needed
                progress?.Report(new UpscalingProgress(UpscalingPhase.Finalizing, "Finalizing...", 95));
                finalImage = upscaled4x.Clone();
            }
            else
            {
                // Downscale from 4x to target using high-quality Lanczos3
                progress?.Report(new UpscalingProgress(
                    UpscalingPhase.ResizingToTarget, 
                    $"Resizing to {targetScale:F1}x ({targetWidth}x{targetHeight})...", 
                    90));

                finalImage = upscaled4x.Clone(ctx => 
                    ctx.Resize(new ResizeOptions
                    {
                        Size = new Size(targetWidth, targetHeight),
                        Sampler = KnownResamplers.Lanczos3,
                        Mode = ResizeMode.Stretch
                    }));
            }

            // Step 4: Encode to PNG
            progress?.Report(new UpscalingProgress(UpscalingPhase.Finalizing, "Encoding result...", 98));
            cancellationToken.ThrowIfCancellationRequested();

            using var outputStream = new MemoryStream();
            finalImage.SaveAsPng(outputStream);
            finalImage.Dispose();

            progress?.Report(new UpscalingProgress(UpscalingPhase.Finalizing, "Complete!", 100));

            return ImageUpscalingResult.Succeeded(
                outputStream.ToArray(),
                targetWidth,
                targetHeight);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Image upscaling failed");
            return ImageUpscalingResult.Failed($"Upscaling failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes the image through the model using overlapping tiles.
    /// </summary>
    private Image<Rgba32> ProcessTiles(
        Image<Rgba32> input,
        IProgress<UpscalingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var inputWidth = input.Width;
        var inputHeight = input.Height;
        var outputWidth = inputWidth * ScaleFactor;
        var outputHeight = inputHeight * ScaleFactor;

        // Calculate tile grid
        var tileStep = TileSize - TileOverlap;
        var tilesX = (int)Math.Ceiling((double)inputWidth / tileStep);
        var tilesY = (int)Math.Ceiling((double)inputHeight / tileStep);
        var totalTiles = tilesX * tilesY;

        Log.Information("Upscaling {Width}x{Height} -> {OutWidth}x{OutHeight} using {TileCount} tiles",
            inputWidth, inputHeight, outputWidth, outputHeight, totalTiles);

        // Create output image
        var output = new Image<Rgba32>(outputWidth, outputHeight);

        var tilesProcessed = 0;

        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Calculate tile bounds in input space
                var inputX = Math.Min(tx * tileStep, inputWidth - TileSize);
                var inputY = Math.Min(ty * tileStep, inputHeight - TileSize);
                inputX = Math.Max(0, inputX);
                inputY = Math.Max(0, inputY);

                // Handle edge tiles that may be smaller
                var actualTileWidth = Math.Min(TileSize, inputWidth - inputX);
                var actualTileHeight = Math.Min(TileSize, inputHeight - inputY);

                // Extract tile (pad if necessary)
                using var tile = ExtractTile(input, inputX, inputY, actualTileWidth, actualTileHeight);

                // Process tile through model
                using var upscaledTile = ProcessSingleTile(tile);

                // Calculate output position
                var outputX = inputX * ScaleFactor;
                var outputY = inputY * ScaleFactor;

                // Blend tile into output (handle overlap blending)
                BlendTileIntoOutput(output, upscaledTile, outputX, outputY, 
                    tx > 0, ty > 0, tx < tilesX - 1, ty < tilesY - 1);

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
    /// Extracts a tile from the input image, padding if necessary.
    /// </summary>
    private static Image<Rgba32> ExtractTile(Image<Rgba32> source, int x, int y, int width, int height)
    {
        var tile = new Image<Rgba32>(TileSize, TileSize);

        source.ProcessPixelRows(tile, (sourceAccessor, tileAccessor) =>
        {
            for (var row = 0; row < TileSize; row++)
            {
                var srcY = Math.Min(y + row, source.Height - 1);
                var srcRow = sourceAccessor.GetRowSpan(srcY);
                var dstRow = tileAccessor.GetRowSpan(row);

                for (var col = 0; col < TileSize; col++)
                {
                    var srcX = Math.Min(x + col, source.Width - 1);
                    dstRow[col] = srcRow[srcX];
                }
            }
        });

        return tile;
    }

    /// <summary>
    /// Processes a single tile through the ONNX model.
    /// </summary>
    private Image<Rgba32> ProcessSingleTile(Image<Rgba32> tile)
    {
        // Create input tensor [1, 3, H, W] normalized to 0-1
        var tensor = new DenseTensor<float>([1, 3, TileSize, TileSize]);

        tile.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < TileSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < TileSize; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = pixel.R / 255.0f;
                    tensor[0, 1, y, x] = pixel.G / 255.0f;
                    tensor[0, 2, y, x] = pixel.B / 255.0f;
                }
            }
        });

        // Run inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(InputName, tensor)
        };

        using var results = _session!.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

        // Convert output tensor to image
        var outputSize = TileSize * ScaleFactor;
        var output = new Image<Rgba32>(outputSize, outputSize);

        output.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < outputSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < outputSize; x++)
                {
                    var r = (byte)Math.Clamp(outputTensor[0, 0, y, x] * 255.0f, 0, 255);
                    var g = (byte)Math.Clamp(outputTensor[0, 1, y, x] * 255.0f, 0, 255);
                    var b = (byte)Math.Clamp(outputTensor[0, 2, y, x] * 255.0f, 0, 255);
                    row[x] = new Rgba32(r, g, b, 255);
                }
            }
        });

        return output;
    }

    /// <summary>
    /// Blends an upscaled tile into the output image with overlap handling.
    /// </summary>
    private static void BlendTileIntoOutput(
        Image<Rgba32> output,
        Image<Rgba32> tile,
        int outputX,
        int outputY,
        bool hasLeftNeighbor,
        bool hasTopNeighbor,
        bool hasRightNeighbor,
        bool hasBottomNeighbor)
    {
        var tileWidth = tile.Width;
        var tileHeight = tile.Height;
        var overlapScaled = TileOverlap * ScaleFactor;

        output.ProcessPixelRows(tile, (outputAccessor, tileAccessor) =>
        {
            for (var ty = 0; ty < tileHeight; ty++)
            {
                var oy = outputY + ty;
                if (oy < 0 || oy >= output.Height) continue;

                var outputRow = outputAccessor.GetRowSpan(oy);
                var tileRow = tileAccessor.GetRowSpan(ty);

                for (var tx = 0; tx < tileWidth; tx++)
                {
                    var ox = outputX + tx;
                    if (ox < 0 || ox >= output.Width) continue;

                    var pixel = tileRow[tx];

                    // Calculate blend weights for overlap regions
                    var blendX = 1.0f;
                    var blendY = 1.0f;

                    if (hasLeftNeighbor && tx < overlapScaled)
                    {
                        blendX = (float)tx / overlapScaled;
                    }
                    else if (hasRightNeighbor && tx >= tileWidth - overlapScaled)
                    {
                        blendX = (float)(tileWidth - tx) / overlapScaled;
                    }

                    if (hasTopNeighbor && ty < overlapScaled)
                    {
                        blendY = (float)ty / overlapScaled;
                    }
                    else if (hasBottomNeighbor && ty >= tileHeight - overlapScaled)
                    {
                        blendY = (float)(tileHeight - ty) / overlapScaled;
                    }

                    var blend = blendX * blendY;

                    if (blend >= 0.999f)
                    {
                        // No blending needed
                        outputRow[ox] = pixel;
                    }
                    else
                    {
                        // Blend with existing pixel
                        var existing = outputRow[ox];
                        var r = (byte)(existing.R * (1 - blend) + pixel.R * blend);
                        var g = (byte)(existing.G * (1 - blend) + pixel.G * blend);
                        var b = (byte)(existing.B * (1 - blend) + pixel.B * blend);
                        outputRow[ox] = new Rgba32(r, g, b, 255);
                    }
                }
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _sessionLock.Wait();
        try
        {
            _session?.Dispose();
            _session = null;
        }
        finally
        {
            _sessionLock.Release();
        }

        _sessionLock.Dispose();
        _disposed = true;
    }
}
