using DiffusionNexus.Domain.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for removing backgrounds from images using the RMBG-1.4 ONNX model.
/// Performs inference locally using GPU acceleration when available, with CPU fallback.
/// </summary>
public sealed class BackgroundRemovalService : IBackgroundRemovalService
{
    private const int ModelInputSize = 1024;
    private const string InputName = "input";
    private const string OutputName = "output";

    private readonly OnnxModelManager _modelManager;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private InferenceSession? _session;
    private bool _isGpuAvailable;
    private bool _isProcessing;
    private bool _disposed;

    /// <summary>
    /// Creates a new BackgroundRemovalService.
    /// </summary>
    public BackgroundRemovalService() : this(new OnnxModelManager()) { }

    /// <summary>
    /// Creates a new BackgroundRemovalService with a custom model manager.
    /// </summary>
    /// <param name="modelManager">The model manager to use.</param>
    public BackgroundRemovalService(OnnxModelManager modelManager)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
    }

    /// <inheritdoc />
    public bool IsGpuAvailable => _isGpuAvailable;

    /// <inheritdoc />
    public bool IsProcessing => _isProcessing;

    /// <inheritdoc />
    public ModelStatus GetModelStatus() => _modelManager.GetRmbg14Status();

    /// <inheritdoc />
    public string GetModelPath() => _modelManager.Rmbg14ModelPath;

    /// <inheritdoc />
    public Task<bool> DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return _modelManager.DownloadRmbg14ModelAsync(progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null)
            return true;

        var status = GetModelStatus();
        if (status != ModelStatus.Ready)
        {
            Log.Warning("Cannot initialize BackgroundRemovalService: model status is {Status}", status);
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
        var modelPath = _modelManager.Rmbg14ModelPath;
        if (!File.Exists(modelPath))
        {
            Log.Error("RMBG-1.4 model file not found: {Path}", modelPath);
            return null;
        }

        // Try GPU first
        try
        {
            var gpuOptions = new SessionOptions();
            gpuOptions.AppendExecutionProvider_CUDA(0);
            gpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            var session = new InferenceSession(modelPath, gpuOptions);
            _isGpuAvailable = true;
            Log.Information("RMBG-1.4 ONNX session created with GPU (CUDA) acceleration");
            return session;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GPU (CUDA) not available, falling back to CPU");
        }

        // Try DirectML (for AMD/Intel GPUs on Windows)
        try
        {
            var dmlOptions = new SessionOptions();
            dmlOptions.AppendExecutionProvider_DML(0);
            dmlOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            var session = new InferenceSession(modelPath, dmlOptions);
            _isGpuAvailable = true;
            Log.Information("RMBG-1.4 ONNX session created with GPU (DirectML) acceleration");
            return session;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DirectML not available, falling back to CPU");
        }

        // Fall back to CPU
        try
        {
            var cpuOptions = new SessionOptions();
            cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            cpuOptions.EnableMemoryPattern = true;
            cpuOptions.EnableCpuMemArena = true;

            var session = new InferenceSession(modelPath, cpuOptions);
            _isGpuAvailable = false;
            Log.Information("RMBG-1.4 ONNX session created with CPU execution");
            return session;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create ONNX session");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<BackgroundRemovalResult> RemoveBackgroundAsync(
        byte[] imageData,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        if (width <= 0 || height <= 0)
            return BackgroundRemovalResult.Failed("Invalid image dimensions");

        if (_isProcessing)
            return BackgroundRemovalResult.Failed("Service is already processing an image");

        // Ensure session is initialized
        if (!await InitializeAsync(cancellationToken))
            return BackgroundRemovalResult.Failed("Failed to initialize ONNX session. Please ensure the model is downloaded.");

        _isProcessing = true;
        try
        {
            return await Task.Run(() => ProcessImage(imageData, width, height, cancellationToken), cancellationToken);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private BackgroundRemovalResult ProcessImage(
        byte[] imageData,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        try
        {
            // Step 1: Load and preprocess image
            cancellationToken.ThrowIfCancellationRequested();
            using var originalImage = Image.LoadPixelData<Rgba32>(imageData, width, height);

            // Step 2: Resize to model input size (1024x1024)
            using var resizedImage = originalImage.Clone(ctx => ctx.Resize(ModelInputSize, ModelInputSize));

            // Step 3: Create input tensor (planar format: [1, 3, 1024, 1024])
            var inputTensor = PreprocessImage(resizedImage);

            // Step 4: Run inference
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(InputName, inputTensor)
            };

            using var results = _session!.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();

            // Step 5: Postprocess - convert output to mask and resize
            cancellationToken.ThrowIfCancellationRequested();
            var maskData = PostprocessOutput(outputTensor, width, height);

            return BackgroundRemovalResult.Succeeded(maskData, width, height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background removal failed");
            return BackgroundRemovalResult.Failed($"Background removal failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Preprocesses the image for RMBG-1.4 model input.
    /// Converts to planar format (RRRGGGBBB) and normalizes to 0-1 range.
    /// </summary>
    private static DenseTensor<float> PreprocessImage(Image<Rgba32> image)
    {
        var tensor = new DenseTensor<float>([1, 3, ModelInputSize, ModelInputSize]);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (var x = 0; x < accessor.Width; x++)
                {
                    var pixel = pixelRow[x];
                    // Normalize 0-255 to 0-1 and arrange in planar format
                    tensor[0, 0, y, x] = pixel.R / 255.0f; // Red channel
                    tensor[0, 1, y, x] = pixel.G / 255.0f; // Green channel
                    tensor[0, 2, y, x] = pixel.B / 255.0f; // Blue channel
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// Postprocesses the model output to create an alpha mask.
    /// Resizes the mask from 1024x1024 back to the original image dimensions.
    /// </summary>
    private static byte[] PostprocessOutput(Tensor<float> outputTensor, int targetWidth, int targetHeight)
    {
        // Create grayscale mask image from output tensor
        using var maskImage = new Image<L8>(ModelInputSize, ModelInputSize);

        // The output is typically [1, 1, H, W] or [1, H, W]
        var dimensions = outputTensor.Dimensions.ToArray();
        var isChannelFirst = dimensions.Length == 4;
        
        maskImage.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < ModelInputSize; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (var x = 0; x < ModelInputSize; x++)
                {
                    float value;
                    if (isChannelFirst)
                    {
                        // [1, 1, H, W] format
                        value = outputTensor[0, 0, y, x];
                    }
                    else
                    {
                        // [1, H, W] format
                        value = outputTensor[0, y, x];
                    }

                    // Clamp and convert to byte
                    var byteValue = (byte)Math.Clamp(value * 255.0f, 0, 255);
                    pixelRow[x] = new L8(byteValue);
                }
            }
        });

        // Resize mask to original image dimensions
        if (targetWidth != ModelInputSize || targetHeight != ModelInputSize)
        {
            maskImage.Mutate(ctx => ctx.Resize(targetWidth, targetHeight));
        }

        // Extract mask data as byte array
        var maskData = new byte[targetWidth * targetHeight];
        maskImage.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (var x = 0; x < accessor.Width; x++)
                {
                    maskData[y * targetWidth + x] = pixelRow[x].PackedValue;
                }
            }
        });

        return maskData;
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
