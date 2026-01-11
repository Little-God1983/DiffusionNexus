using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using LLama;
using LLama.Common;
using LLama.Native;
using Serilog;

namespace DiffusionNexus.Captioning;

/// <summary>
/// Service for generating image captions using local vision-language models.
/// Uses LlamaSharp with CUDA 12 backend for NVIDIA GPU acceleration.
/// </summary>
public sealed class CaptioningService : ICaptioningService
{
    private readonly CaptioningModelManager _modelManager;
    private readonly SemaphoreSlim _inferencelock = new(1, 1);
    
    private LLamaWeights? _modelWeights;
    private LLamaContext? _context;
    private LLavaWeights? _clipWeights;
    private CaptioningModelType? _loadedModelType;
    private bool _isProcessing;
    private bool _isGpuAvailable;
    private bool _disposed;

    /// <summary>
    /// Creates a new CaptioningService.
    /// </summary>
    public CaptioningService() : this(new CaptioningModelManager()) { }

    /// <summary>
    /// Creates a new CaptioningService with a custom model manager.
    /// </summary>
    public CaptioningService(CaptioningModelManager modelManager)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
        InitializeNativeLibrary();
    }

    /// <inheritdoc />
    public bool IsProcessing => _isProcessing;

    /// <inheritdoc />
    public bool IsModelLoaded => _modelWeights is not null && _context is not null;

    /// <inheritdoc />
    public CaptioningModelType? LoadedModelType => _loadedModelType;

    /// <inheritdoc />
    public bool IsGpuAvailable => _isGpuAvailable;

    /// <summary>
    /// Initializes the LLama native library.
    /// </summary>
    private void InitializeNativeLibrary()
    {
        try
        {
            // Initialize LLama native library - this will use CUDA if available
            NativeLibraryConfig.LLama.WithLogCallback((level, message) =>
            {
                // Route LLama logs to Serilog
                var logLevel = level switch
                {
                    LLamaLogLevel.Error => Serilog.Events.LogEventLevel.Error,
                    LLamaLogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
                    LLamaLogLevel.Info => Serilog.Events.LogEventLevel.Information,
                    _ => Serilog.Events.LogEventLevel.Debug
                };
                Log.Write(logLevel, "[LLama] {Message}", message?.TrimEnd());
            });

            // Check for CUDA availability
            _isGpuAvailable = NativeApi.llama_supports_gpu_offload();
            
            if (_isGpuAvailable)
            {
                Log.Information("LlamaSharp CUDA GPU acceleration is available");
            }
            else
            {
                Log.Warning("LlamaSharp CUDA GPU acceleration is NOT available. Caption generation will be slow on CPU.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize LLama native library");
            _isGpuAvailable = false;
        }
    }

    /// <inheritdoc />
    public CaptioningModelInfo GetModelInfo(CaptioningModelType modelType)
    {
        var info = _modelManager.GetModelInfo(modelType);
        
        // Update status if this model is currently loaded
        if (_loadedModelType == modelType && IsModelLoaded)
        {
            return info with { Status = CaptioningModelStatus.Loaded };
        }
        
        return info;
    }

    /// <inheritdoc />
    public IReadOnlyList<CaptioningModelInfo> GetAllModels()
    {
        return Enum.GetValues<CaptioningModelType>()
            .Select(GetModelInfo)
            .ToList();
    }

    /// <inheritdoc />
    public Task<bool> DownloadModelAsync(
        CaptioningModelType modelType,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return _modelManager.DownloadModelAsync(modelType, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> LoadModelAsync(
        CaptioningModelType modelType,
        CancellationToken cancellationToken = default)
    {
        if (_loadedModelType == modelType && IsModelLoaded)
        {
            Log.Debug("Model {ModelType} is already loaded", modelType);
            return true;
        }

        var status = _modelManager.GetModelStatus(modelType);
        if (status != CaptioningModelStatus.Ready)
        {
            Log.Warning("Cannot load model {ModelType}: status is {Status}", modelType, status);
            return false;
        }

        await _inferencelock.WaitAsync(cancellationToken);
        try
        {
            // Unload any existing model
            UnloadModelInternal();

            var modelPath = _modelManager.GetModelPath(modelType);
            var clipPath = _modelManager.GetClipProjectorPath(modelType);

            Log.Information("Loading {ModelType} model from {Path}", modelType, modelPath);

            // Configure model parameters for maximum GPU utilization
            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = 4096,
                GpuLayerCount = _isGpuAvailable ? -1 : 0, // -1 = all layers on GPU
                Seed = 0, // Random seed
                UseMemorymap = true,
                UseMemoryLock = false,
            };

            // Load model weights
            _modelWeights = await Task.Run(() => LLamaWeights.LoadFromFile(modelParams), cancellationToken);

            // Create context
            _context = _modelWeights.CreateContext(modelParams);

            // Load CLIP weights for vision
            Log.Information("Loading CLIP projector from {Path}", clipPath);
            _clipWeights = await Task.Run(() => LLavaWeights.LoadFromFile(clipPath), cancellationToken);

            _loadedModelType = modelType;
            
            Log.Information("Successfully loaded {ModelType} model", modelType);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load model {ModelType}", modelType);
            UnloadModelInternal();
            return false;
        }
        finally
        {
            _inferencelock.Release();
        }
    }

    /// <inheritdoc />
    public void UnloadModel()
    {
        _inferencelock.Wait();
        try
        {
            UnloadModelInternal();
        }
        finally
        {
            _inferencelock.Release();
        }
    }

    private void UnloadModelInternal()
    {
        if (_loadedModelType.HasValue)
        {
            Log.Information("Unloading model {ModelType}", _loadedModelType);
        }

        _clipWeights?.Dispose();
        _clipWeights = null;

        _context?.Dispose();
        _context = null;

        _modelWeights?.Dispose();
        _modelWeights = null;

        _loadedModelType = null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaptioningResult>> GenerateCaptionsAsync(
        CaptioningJobConfig config,
        IProgress<CaptioningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var validationErrors = config.Validate();
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException($"Invalid configuration: {string.Join("; ", validationErrors)}");
        }

        if (_isProcessing)
        {
            throw new InvalidOperationException("Service is already processing a batch.");
        }

        // Ensure model is loaded
        if (!IsModelLoaded || _loadedModelType != config.SelectedModel)
        {
            var loaded = await LoadModelAsync(config.SelectedModel, cancellationToken);
            if (!loaded)
            {
                throw new InvalidOperationException($"Failed to load model {config.SelectedModel}");
            }
        }

        _isProcessing = true;
        var results = new List<CaptioningResult>();

        try
        {
            var imagePaths = config.ImagePaths.ToList();
            var totalCount = imagePaths.Count;

            for (var i = 0; i < totalCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var imagePath = imagePaths[i];
                
                progress?.Report(new CaptioningProgress(
                    i, totalCount, imagePath,
                    $"Processing {i + 1}/{totalCount}: {Path.GetFileName(imagePath)}"));

                // Check if caption already exists
                var captionFilePath = GetCaptionFilePath(imagePath, config.DatasetPath);
                if (!config.OverrideExisting && File.Exists(captionFilePath))
                {
                    var skipResult = CaptioningResult.Skipped(imagePath, "Caption file already exists");
                    results.Add(skipResult);
                    
                    progress?.Report(new CaptioningProgress(
                        i + 1, totalCount, imagePath,
                        $"Skipped {i + 1}/{totalCount}: {Path.GetFileName(imagePath)}",
                        skipResult));
                    continue;
                }

                var result = await GenerateSingleCaptionInternalAsync(
                    imagePath,
                    config.SystemPrompt,
                    config.TriggerWord,
                    config.BlacklistedWords,
                    config.Temperature,
                    captionFilePath,
                    cancellationToken);

                results.Add(result);

                progress?.Report(new CaptioningProgress(
                    i + 1, totalCount, imagePath,
                    $"Completed {i + 1}/{totalCount}: {Path.GetFileName(imagePath)}",
                    result));
            }

            return results;
        }
        finally
        {
            _isProcessing = false;
        }
    }

    /// <inheritdoc />
    public async Task<CaptioningResult> GenerateSingleCaptionAsync(
        string imagePath,
        string systemPrompt,
        string? triggerWord = null,
        IReadOnlyList<string>? blacklistedWords = null,
        float temperature = 0.7f,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return CaptioningResult.Failed(imagePath, "Image path is required.");

        if (!IsModelLoaded)
            return CaptioningResult.Failed(imagePath, "No model is loaded. Call LoadModelAsync first.");

        return await GenerateSingleCaptionInternalAsync(
            imagePath, systemPrompt, triggerWord, blacklistedWords, temperature, null, cancellationToken);
    }

    private async Task<CaptioningResult> GenerateSingleCaptionInternalAsync(
        string imagePath,
        string systemPrompt,
        string? triggerWord,
        IReadOnlyList<string>? blacklistedWords,
        float temperature,
        string? captionFilePath,
        CancellationToken cancellationToken)
    {
        try
        {
            // Validate and preprocess image
            var preprocessResult = ImagePreprocessor.ProcessImage(imagePath);
            if (!preprocessResult.Success)
            {
                return CaptioningResult.Failed(imagePath, preprocessResult.ErrorMessage ?? "Failed to preprocess image.");
            }

            await _inferencelock.WaitAsync(cancellationToken);
            try
            {
                if (_context is null || _clipWeights is null || _modelWeights is null)
                {
                    return CaptioningResult.Failed(imagePath, "Model is not loaded.");
                }

                // Build prompt based on model type
                var prompt = BuildPrompt(_loadedModelType!.Value, systemPrompt);

                // Create image embedding
                var imageEmbedding = _clipWeights.CreateImageEmbeddings(_context, preprocessResult.ImageData!);

                // Create inference parameters
                var inferenceParams = new InferenceParams
                {
                    Temperature = temperature,
                    MaxTokens = 512,
                    AntiPrompts = GetAntiPrompts(_loadedModelType.Value)
                };

                // Create executor
                var executor = new InteractiveExecutor(_context);

                // Process the image with the prompt
                var imageWithPrompt = _clipWeights.CreateImagePlaceholderForPrompt();
                var fullPrompt = prompt.Replace("<image>", imageWithPrompt);

                // Run inference
                var caption = new System.Text.StringBuilder();
                
                await foreach (var token in executor.InferAsync(fullPrompt, inferenceParams, cancellationToken))
                {
                    caption.Append(token);
                }

                // Post-process caption
                var finalCaption = PostProcessCaption(
                    caption.ToString(),
                    triggerWord,
                    blacklistedWords);

                // Save caption to file if path provided
                if (!string.IsNullOrEmpty(captionFilePath))
                {
                    var directory = Path.GetDirectoryName(captionFilePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    await File.WriteAllTextAsync(captionFilePath, finalCaption, cancellationToken);
                }

                return CaptioningResult.Succeeded(imagePath, finalCaption, captionFilePath ?? string.Empty);
            }
            finally
            {
                _inferencelock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error generating caption for {ImagePath}", imagePath);
            return CaptioningResult.Failed(imagePath, $"Error generating caption: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the prompt based on the model type.
    /// </summary>
    private static string BuildPrompt(CaptioningModelType modelType, string systemPrompt)
    {
        return modelType switch
        {
            // LLaVA uses Vicuna-style prompt format
            CaptioningModelType.LLaVA_v1_6_34B => $"""
                A chat between a curious user and an artificial intelligence assistant. The assistant gives helpful, detailed, and polite answers to the user's questions.
                USER: <image>
                {systemPrompt}
                ASSISTANT:
                """,

            // Qwen uses ChatML format
            CaptioningModelType.Qwen3_VL_8B => $"""
                <|im_start|>system
                You are a helpful assistant.<|im_end|>
                <|im_start|>user
                <image>
                {systemPrompt}<|im_end|>
                <|im_start|>assistant
                """,

            _ => throw new ArgumentOutOfRangeException(nameof(modelType))
        };
    }

    /// <summary>
    /// Gets anti-prompts (stop sequences) for a model type.
    /// </summary>
    private static List<string> GetAntiPrompts(CaptioningModelType modelType)
    {
        return modelType switch
        {
            CaptioningModelType.LLaVA_v1_6_34B => ["USER:", "</s>"],
            CaptioningModelType.Qwen3_VL_8B => ["<|im_end|>", "<|im_start|>", "<|endoftext|>"],
            _ => []
        };
    }

    /// <summary>
    /// Post-processes the generated caption.
    /// </summary>
    private static string PostProcessCaption(
        string caption,
        string? triggerWord,
        IReadOnlyList<string>? blacklistedWords)
    {
        // Clean up the caption
        var result = caption.Trim();

        // Remove any remaining special tokens
        result = result
            .Replace("<|im_end|>", "")
            .Replace("<|im_start|>", "")
            .Replace("</s>", "")
            .Replace("USER:", "")
            .Replace("ASSISTANT:", "")
            .Trim();

        // Remove blacklisted words
        if (blacklistedWords is { Count: > 0 })
        {
            foreach (var word in blacklistedWords)
            {
                // Case-insensitive word replacement
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    $@"\b{System.Text.RegularExpressions.Regex.Escape(word)}\b",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            // Clean up extra whitespace after removals
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();
        }

        // Prepend trigger word if specified
        if (!string.IsNullOrWhiteSpace(triggerWord))
        {
            result = $"{triggerWord.Trim()}, {result}";
        }

        return result;
    }

    /// <summary>
    /// Gets the caption file path for an image.
    /// </summary>
    private static string GetCaptionFilePath(string imagePath, string? datasetPath)
    {
        var imageFileName = Path.GetFileNameWithoutExtension(imagePath);
        
        if (!string.IsNullOrEmpty(datasetPath))
        {
            return Path.Combine(datasetPath, $"{imageFileName}.txt");
        }
        
        var imageDirectory = Path.GetDirectoryName(imagePath) ?? ".";
        return Path.Combine(imageDirectory, $"{imageFileName}.txt");
    }

    /// <inheritdoc />
    public void DeleteModel(CaptioningModelType modelType)
    {
        // Unload if this model is currently loaded
        if (_loadedModelType == modelType)
        {
            UnloadModel();
        }

        _modelManager.DeleteModel(modelType);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _inferencelock.Wait();
        try
        {
            UnloadModelInternal();
        }
        finally
        {
            _inferencelock.Release();
        }

        _inferencelock.Dispose();
        _disposed = true;
    }
}
