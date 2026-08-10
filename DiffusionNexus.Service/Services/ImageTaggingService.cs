using DiffusionNexus.Domain.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Tags images using the WD14 ViT Tagger v3 ONNX model. Performs inference
/// locally with GPU acceleration when available, CPU fallback otherwise —
/// structurally mirrors <see cref="BackgroundRemovalService"/>.
/// </summary>
public sealed class ImageTaggingService : IImageTaggingService
{
    // WD14 selected_tags.csv schema: tag_id,name,category,count.
    // category 9 = rating, 0 = general, 4 = character — the standard
    // WD14/SmilingWolf tag-list schema shared across the whole tagger family.
    private const string RatingCategory = "9";
    private const string GeneralCategory = "0";
    private const string CharacterCategory = "4";

    // The one hardcoded rating-name assumption in this class: WD14/Danbooru's
    // rating taxonomy has used "general" as the safest bucket since the
    // schema's introduction. Every other tag/rating name is read from the CSV.
    private const string SfwRatingLabel = "general";

    private readonly OnnxModelManager _modelManager;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private InferenceSession? _session;
    private string? _inputName;
    private string? _outputName;
    private int _inputSize;
    private List<(string Name, string Category)>? _tagList;
    private bool _isGpuAvailable;
    private bool _isProcessing;
    private bool _disposed;
    private bool _disableGpu;

    public ImageTaggingService() : this(new OnnxModelManager()) { }

    public ImageTaggingService(OnnxModelManager modelManager)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
    }

    public bool IsGpuAvailable => _isGpuAvailable;
    public bool IsProcessing => _isProcessing;

    public ModelStatus GetModelStatus() => _modelManager.GetWd14TaggerStatus();

    public Task<bool> DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _modelManager.DownloadWd14TaggerModelAsync(progress, cancellationToken);

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null)
            return true;

        var status = GetModelStatus();
        if (status != ModelStatus.Ready)
        {
            Log.Warning("Cannot initialize ImageTaggingService: model status is {Status}", status);
            return false;
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null)
                return true;

            try
            {
                _tagList = await Task.Run(() =>
                {
                    using var reader = new StreamReader(_modelManager.Wd14TaggerTagsPath);
                    return LoadTagList(reader);
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Mirrors CreateSession()'s own defensive catch: a failure here (file
                // deleted mid-race, permissions, or a CSV that parses to zero rows
                // despite passing the coarse size check in GetWd14TaggerStatus) must
                // surface as InitializeAsync returning false — not as an exception
                // escaping past TagImageAsync's Task<ImageTagResult> contract.
                Log.Error(ex, "Failed to load WD14 tagger tag list: {Path}", _modelManager.Wd14TaggerTagsPath);
                return false;
            }

            _session = await Task.Run(CreateSession, cancellationToken);
            return _session is not null;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Parses a WD14 <c>selected_tags.csv</c> stream into (name, category) pairs.</summary>
    internal static List<(string Name, string Category)> LoadTagList(TextReader reader)
    {
        var result = new List<(string, string)>();

        var header = reader.ReadLine();
        if (header is null)
            throw new InvalidOperationException("Tag list is empty");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            result.Add((parts[1].Trim(), parts[2].Trim()));
        }

        if (result.Count == 0)
            throw new InvalidOperationException("Tag list parsed to zero entries");

        return result;
    }

    /// <summary>
    /// Pure selection logic: argmax over rating-category scores, threshold
    /// filter over general/character-category scores. Split out from the ONNX
    /// pipeline so it's unit-testable without a model or an image.
    /// </summary>
    internal static (List<ImageTagScore> Tags, string? RatingLabel, float RatingScore) SelectTagsAndRating(
        IReadOnlyList<(string Name, string Category)> tagList,
        IReadOnlyList<float> scores,
        float tagConfidenceThreshold)
    {
        if (scores.Count != tagList.Count)
            throw new InvalidOperationException(
                $"Model output size ({scores.Count}) does not match tag list size ({tagList.Count})");

        var tags = new List<ImageTagScore>();
        string? bestRatingName = null;
        var bestRatingScore = -1f;

        for (var i = 0; i < tagList.Count; i++)
        {
            var (name, category) = tagList[i];
            var score = scores[i];

            if (category == RatingCategory)
            {
                if (score > bestRatingScore)
                {
                    bestRatingScore = score;
                    bestRatingName = name;
                }
                continue;
            }

            if ((category == GeneralCategory || category == CharacterCategory) && score >= tagConfidenceThreshold)
            {
                tags.Add(new ImageTagScore(name, score));
            }
        }

        return (tags.OrderByDescending(t => t.Confidence).ToList(), bestRatingName, bestRatingScore);
    }

    private InferenceSession? CreateSession()
    {
        var modelPath = _modelManager.Wd14TaggerModelPath;
        if (!File.Exists(modelPath))
        {
            Log.Error("WD14 tagger model file not found: {Path}", modelPath);
            return null;
        }

        if (!_disableGpu)
        {
            try
            {
                var dmlOptions = new SessionOptions();
                dmlOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
                dmlOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                dmlOptions.EnableMemoryPattern = false;
                dmlOptions.EnableCpuMemArena = false;
                dmlOptions.AppendExecutionProvider_DML(0);
                dmlOptions.AppendExecutionProvider_CPU(0);

                var session = new InferenceSession(modelPath, dmlOptions);
                _isGpuAvailable = true;
                DiscoverModelShape(session);
                Log.Information("WD14 tagger ONNX session created with GPU (DirectML) acceleration");
                return session;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DirectML not available or failed to initialize, falling back to CPU");
            }
        }

        try
        {
            var cpuOptions = new SessionOptions();
            cpuOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            var session = new InferenceSession(modelPath, cpuOptions);
            _isGpuAvailable = false;
            DiscoverModelShape(session);
            Log.Information("WD14 tagger ONNX session created with CPU execution");
            return session;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create WD14 tagger ONNX session");
            return null;
        }
    }

    /// <summary>
    /// Reads input/output tensor names and the model's square input size from
    /// its own metadata rather than hardcoding them — WD14 tagger variants
    /// (ViT/SwinV2/EVA02) don't necessarily share identical export conventions.
    /// </summary>
    private void DiscoverModelShape(InferenceSession session)
    {
        _inputName = session.InputMetadata.Keys.FirstOrDefault();
        _outputName = session.OutputMetadata.Keys.FirstOrDefault();

        if (string.IsNullOrEmpty(_inputName) || string.IsNullOrEmpty(_outputName))
            throw new InvalidOperationException("Could not determine WD14 tagger input/output tensor names from model metadata");

        var dims = session.InputMetadata[_inputName].Dimensions;
        // NHWC: [batch, height, width, channels] — the spatial dimension is
        // whichever positive dim isn't the batch (1) or channel (3) axis.
        _inputSize = dims.FirstOrDefault(d => d > 0 && d != 1 && d != 3);
        if (_inputSize <= 0)
            throw new InvalidOperationException("Could not determine WD14 tagger input size from model metadata");
    }

    public async Task<ImageTagResult> TagImageAsync(
        byte[] imageData,
        int width,
        int height,
        float tagConfidenceThreshold = 0.35f,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        if (width <= 0 || height <= 0)
            return ImageTagResult.Failed("Invalid image dimensions");

        if (_isProcessing)
            return ImageTagResult.Failed("Service is already processing an image");

        if (!await InitializeAsync(cancellationToken))
            return ImageTagResult.Failed("Failed to initialize ONNX session. Please ensure the model is downloaded.");

        _isProcessing = true;
        try
        {
            return await Task.Run(() => ProcessImage(imageData, width, height, tagConfidenceThreshold, cancellationToken), cancellationToken);
        }
        catch (OnnxRuntimeException ex) when (_isGpuAvailable && !_disableGpu)
        {
            Log.Warning("GPU inference failed for WD14 tagger, disabling GPU and retrying on CPU. Error: {Error}", ex.Message);

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

            if (await InitializeAsync(cancellationToken))
            {
                try
                {
                    return await Task.Run(() => ProcessImage(imageData, width, height, tagConfidenceThreshold, cancellationToken), cancellationToken);
                }
                catch (Exception retryEx)
                {
                    return ImageTagResult.Failed($"Tagging failed (CPU retry): {retryEx.Message}");
                }
            }

            return ImageTagResult.Failed($"Tagging failed (GPU error): {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Image tagging failed");
            return ImageTagResult.Failed($"Tagging failed: {ex.Message}");
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private ImageTagResult ProcessImage(
        byte[] imageData, int width, int height, float tagConfidenceThreshold, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var original = Image.LoadPixelData<Rgba32>(imageData, width, height);

        // Pad to square (white background, matching WD14's own training
        // preprocessing) before resizing to the model's expected input size —
        // avoids distorting aspect ratio on non-square gallery images.
        var side = Math.Max(original.Width, original.Height);
        using var squared = new Image<Rgba32>(side, side, new Rgba32(255, 255, 255, 255));
        squared.Mutate(ctx => ctx.DrawImage(original, new Point(
            (side - original.Width) / 2, (side - original.Height) / 2), 1f));
        using var resized = squared.Clone(ctx => ctx.Resize(_inputSize, _inputSize));

        var inputTensor = PreprocessImage(resized);

        cancellationToken.ThrowIfCancellationRequested();
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName!, inputTensor) };
        using var results = _session!.Run(inputs);
        var scores = results.First().AsTensor<float>().ToArray();

        var (tags, ratingLabel, ratingScore) = SelectTagsAndRating(_tagList!, scores, tagConfidenceThreshold);

        if (ratingLabel is null)
            return ImageTagResult.Failed("Tag list contains no rating (category 9) entries");

        var isNsfw = !string.Equals(ratingLabel, SfwRatingLabel, StringComparison.OrdinalIgnoreCase);
        return ImageTagResult.Succeeded(tags, ratingLabel, ratingScore, isNsfw);
    }

    /// <summary>
    /// WD14 taggers expect NHWC, BGR channel order, raw 0-255 float values —
    /// NOT normalized to 0-1. This is a well-documented convention for this
    /// model family (unlike most torchvision-style classifiers); verify
    /// against real sample output before trusting this in production (see
    /// the manual verification task in this plan).
    /// </summary>
    private DenseTensor<float> PreprocessImage(Image<Rgba32> image)
    {
        var tensor = new DenseTensor<float>([1, _inputSize, _inputSize, 3]);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < accessor.Width; x++)
                {
                    var pixel = row[x];
                    tensor[0, y, x, 0] = pixel.B;
                    tensor[0, y, x, 1] = pixel.G;
                    tensor[0, y, x, 2] = pixel.R;
                }
            }
        });

        return tensor;
    }

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
