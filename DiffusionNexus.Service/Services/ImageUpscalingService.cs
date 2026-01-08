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
    private const int TilePadding = 32; // Padding around tile (context) to avoid edge artifacts
    private const int TileParseSize = TileSize - 2 * TilePadding; // The actual valid content size per tile

    private readonly OnnxModelManager _modelManager;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private InferenceSession? _session;
    private string? _inputName;
    private string? _outputName;
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
                
                // Compatibility parameters for DirectML on 4x-UltraSharp
                // These settings prioritize stability over performance to prevent known DirectML issues.
                dmlOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
                dmlOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                
                dmlOptions.EnableMemoryPattern = false;
                dmlOptions.EnableCpuMemArena = false;
                
                dmlOptions.AddSessionConfigEntry("session.disable_prepacking", "1");
                dmlOptions.AddSessionConfigEntry("ep.dml.enable_graph_capture", "0");
                
                // Add DirectML as primary execution provider
                dmlOptions.AppendExecutionProvider_DML(0);
                
                // Add CPU as fallback for operations DirectML doesn't support
                dmlOptions.AppendExecutionProvider_CPU(0);

                var session = new InferenceSession(modelPath, dmlOptions);
                _isGpuAvailable = true;

                // Discover input/output names from model metadata
                DiscoverTensorNames(session);

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

            // Discover input/output names from model metadata
            DiscoverTensorNames(session);

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

    /// <summary>
    /// Discovers the actual input and output tensor names from the model metadata.
    /// </summary>
    private void DiscoverTensorNames(InferenceSession session)
    {
        // Get input name from model metadata
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

        // Calculate tile grid using ParseSize (stride)
        var tilesX = (int)Math.Ceiling((double)inputWidth / TileParseSize);
        var tilesY = (int)Math.Ceiling((double)inputHeight / TileParseSize);
        var totalTiles = tilesX * tilesY;

        Log.Information("Upscaling {Width}x{Height} -> {OutWidth}x{OutHeight} using {TileCount} tiles (Padding: {Padding})",
            inputWidth, inputHeight, outputWidth, outputHeight, totalTiles, TilePadding);

        // Create output image
        var output = new Image<Rgba32>(outputWidth, outputHeight);

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
    private static Image<Rgba32> ExtractTile(Image<Rgba32> source, int x, int y)
    {
        var tile = new Image<Rgba32>(TileSize, TileSize);

        source.ProcessPixelRows(tile, (sourceAccessor, tileAccessor) =>
        {
            for (var row = 0; row < TileSize; row++)
            {
                // Clamp Y to valid source image range (Edge Replication)
                var srcY = Math.Clamp(y + row, 0, source.Height - 1);
                var srcRow = sourceAccessor.GetRowSpan(srcY);
                var dstRow = tileAccessor.GetRowSpan(row);

                for (var col = 0; col < TileSize; col++)
                {
                    // Clamp X to valid source image range
                    var srcX = Math.Clamp(x + col, 0, source.Width - 1);
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

        // Run inference using discovered input name
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName!, tensor)
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
    /// Copies the valid central region of an upscaled tile to the output image.
    /// </summary>
    private static void CopyTileToOutput(
        Image<Rgba32> output,
        Image<Rgba32> tile,
        int destX,
        int destY,
        int srcX,
        int srcY,
        int width,
        int height)
    {
        output.ProcessPixelRows(tile, (outputAccessor, tileAccessor) =>
        {
            for (var row = 0; row < height; row++)
            {
                var dy = destY + row;
                if (dy >= output.Height) break; // Clip bottom edge

                var sy = srcY + row;
                var outputRow = outputAccessor.GetRowSpan(dy);
                var tileRow = tileAccessor.GetRowSpan(sy);

                var copyWidth = Math.Min(width, output.Width - destX); // Clip right edge
                if (copyWidth <= 0) continue;

                tileRow.Slice(srcX, copyWidth).CopyTo(outputRow.Slice(destX, copyWidth));
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
