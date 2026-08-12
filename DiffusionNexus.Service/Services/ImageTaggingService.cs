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

    private readonly OnnxModelManager _modelManager;
    private readonly OnnxSessionHost _host = new("WD14 tagger");
    private string? _inputName;
    private string? _outputName;
    private int _inputSize;
    private List<(string Name, string Category)>? _tagList;

    public ImageTaggingService() : this(new OnnxModelManager()) { }

    public ImageTaggingService(OnnxModelManager modelManager)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
    }

    public bool IsGpuAvailable => _host.IsGpuAvailable;
    public bool IsProcessing => _host.IsProcessing;

    public ModelStatus GetModelStatus() => _modelManager.GetWd14TaggerStatus();

    public Task<bool> DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _modelManager.DownloadWd14TaggerModelAsync(progress, cancellationToken);

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_host.IsInitialized && _tagList is not null)
            return true;

        var status = GetModelStatus();
        if (status != ModelStatus.Ready)
        {
            Log.Warning("Cannot initialize ImageTaggingService: model status is {Status}", status);
            return false;
        }

        if (_tagList is null)
        {
            try
            {
                // Concurrent callers may both load the CSV; the assignment is an
                // atomic reference swap of identical content, so last-wins is fine.
                _tagList = await Task.Run(() =>
                {
                    using var reader = new StreamReader(_modelManager.Wd14TaggerTagsPath);
                    return LoadTagList(reader);
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Mirrors the session host's own defensive catch: a failure here
                // (file deleted mid-race, permissions, or a CSV that parses to zero
                // rows despite passing the coarse size check in GetWd14TaggerStatus)
                // must surface as InitializeAsync returning false — not as an
                // exception escaping past TagImageAsync's Task<ImageTagResult>
                // contract.
                Log.Error(ex, "Failed to load WD14 tagger tag list: {Path}", _modelManager.Wd14TaggerTagsPath);
                return false;
            }
        }

        return await _host.InitializeAsync(_modelManager.Wd14TaggerModelPath, DiscoverModelShape, cancellationToken);
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
        string imagePath,
        float tagConfidenceThreshold = 0.35f,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (_host.IsDisposed)
            return ImageTagResult.Failed("Service is disposed");

        if (!_host.TryBeginProcessing())
            return ImageTagResult.Failed("Service is already processing an image");

        try
        {
            if (!await InitializeAsync(cancellationToken))
                return ImageTagResult.Failed("Failed to initialize ONNX session. Please ensure the model is downloaded.");

            try
            {
                return await Task.Run(() => ProcessImage(imagePath, tagConfidenceThreshold, cancellationToken), cancellationToken);
            }
            catch (OnnxRuntimeException ex) when (_host.CanRetryOnCpu)
            {
                Log.Warning("GPU inference failed for WD14 tagger, disabling GPU and retrying on CPU. Error: {Error}", ex.Message);

                await _host.DemoteToCpuAsync(cancellationToken);

                if (await InitializeAsync(cancellationToken))
                {
                    try
                    {
                        return await Task.Run(() => ProcessImage(imagePath, tagConfidenceThreshold, cancellationToken), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception retryEx)
                    {
                        return ImageTagResult.Failed($"Tagging failed (CPU retry): {retryEx.Message}");
                    }
                }

                return ImageTagResult.Failed($"Tagging failed (GPU error): {ex.Message}");
            }
            // One cancellation policy for the whole class: InitializeAsync lets
            // OperationCanceledException propagate raw, so TagImageAsync must too.
            // Without this the general catch below would quietly turn a cancelled
            // batch into a stream of "Failed" results, which callers cannot tell
            // apart from real tagging errors.
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Image tagging failed");
                return ImageTagResult.Failed($"Tagging failed: {ex.Message}");
            }
        }
        finally
        {
            _host.EndProcessing();
        }
    }

    private ImageTagResult ProcessImage(
        string imagePath, float tagConfidenceThreshold, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(imagePath))
            return ImageTagResult.Failed($"Image file not found: {imagePath}");

        // Decode here rather than accepting a caller-supplied pixel buffer: the
        // model only ever sees a ~448² square, so the decoded full-resolution
        // image should be the single large allocation on this path.
        using var original = Image.Load<Rgba32>(imagePath);
        using var prepared = ResizeAndPadToSquare(original, _inputSize);

        var inputTensor = PreprocessImage(prepared);

        cancellationToken.ThrowIfCancellationRequested();
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName!, inputTensor) };
        var scores = _host.Run(inputs, results => results.First().AsTensor<float>().ToArray());

        var (tags, ratingLabel, ratingScore) = SelectTagsAndRating(_tagList!, scores, tagConfidenceThreshold);

        if (ratingLabel is null)
            return ImageTagResult.Failed("Tag list contains no rating (category 9) entries");

        var isNsfw = ContentRatingPolicy.IsNsfw(ratingLabel);
        return ImageTagResult.Succeeded(tags, ratingLabel, ratingScore, isNsfw);
    }

    /// <summary>
    /// Produces the model's square input: the source scaled so its long edge
    /// is exactly <paramref name="size"/>, centered on a white background
    /// (matching WD14's own training preprocessing), aspect ratio preserved.
    /// </summary>
    /// <remarks>
    /// Deliberately resize-then-pad, not pad-then-resize. Both orderings yield
    /// the same centered, white-matted <paramref name="size"/>² image, but
    /// padding first allocates a max(width, height)² intermediate: a 6000×4000
    /// upscaler output — ordinary content for this app's gallery — costs
    /// ~144 MB for that one buffer, per image, in a loop over the whole
    /// gallery. Scaling first caps the intermediate at the source itself.
    /// </remarks>
    internal static Image<Rgba32> ResizeAndPadToSquare(Image<Rgba32> source, int size)
    {
        var scale = (double)size / Math.Max(source.Width, source.Height);
        var scaledWidth = Math.Clamp((int)Math.Round(source.Width * scale), 1, size);
        var scaledHeight = Math.Clamp((int)Math.Round(source.Height * scale), 1, size);

        using var scaled = source.Clone(ctx => ctx.Resize(scaledWidth, scaledHeight));

        var padded = new Image<Rgba32>(source.Configuration, size, size, new Rgba32(255, 255, 255, 255));
        try
        {
            padded.Mutate(ctx => ctx.DrawImage(scaled, new Point(
                (size - scaled.Width) / 2, (size - scaled.Height) / 2), 1f));
            return padded;
        }
        catch
        {
            padded.Dispose();
            throw;
        }
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
        // Write through the contiguous backing buffer: Tensor<T>'s only int
        // indexer is `this[params int[]]`, which would allocate an int[4] for
        // every one of the ~600k pixel-channel writes per image.
        var buffer = tensor.Buffer;

        image.ProcessPixelRows(accessor =>
        {
            var span = buffer.Span;
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * accessor.Width * 3;
                for (var x = 0; x < accessor.Width; x++)
                {
                    var pixel = row[x];
                    span[offset++] = pixel.B;
                    span[offset++] = pixel.G;
                    span[offset++] = pixel.R;
                }
            }
        });

        return tensor;
    }

    public void Dispose() => _host.Dispose();
}
