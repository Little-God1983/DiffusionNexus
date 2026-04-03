using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Captioning backend that delegates to a ComfyUI server running the Qwen3-VL workflow.
/// Images are passed via <c>source_path</c> (local filesystem path), so the ComfyUI server
/// must have access to the same file system as this application.
/// </summary>
public sealed class ComfyUICaptioningBackend : ICaptioningBackend
{
    private static readonly ILogger Logger = Log.ForContext<ComfyUICaptioningBackend>();

    private readonly IComfyUIWrapperService _comfyUi;
    private readonly IComfyUIReadinessService? _readinessService;

    /// <summary>
    /// Creates a new ComfyUI captioning backend.
    /// </summary>
    /// <param name="comfyUiService">The ComfyUI wrapper service to delegate to.</param>
    /// <param name="readinessService">Optional unified readiness service. When provided, <see cref="IsAvailableAsync"/> delegates to it.</param>
    public ComfyUICaptioningBackend(
        IComfyUIWrapperService comfyUiService,
        IComfyUIReadinessService? readinessService = null)
    {
        ArgumentNullException.ThrowIfNull(comfyUiService);
        _comfyUi = comfyUiService;
        _readinessService = readinessService;
    }

    /// <inheritdoc />
    public string DisplayName => "ComfyUI – Qwen3-VL";

    /// <inheritdoc />
    public IReadOnlyList<string> MissingRequirements { get; private set; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_readinessService is null)
        {
            Logger.Warning("No readiness service configured for ComfyUI captioning backend");
            MissingRequirements = ["ComfyUI readiness service is not configured."];
            Warnings = [];
            return false;
        }

        try
        {
            var result = await _readinessService.CheckFeatureAsync(ComfyUIFeature.Captioning, ct);

            MissingRequirements = result.MissingRequirements;
            Warnings = result.Warnings;

            return result.IsReady;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Unified readiness check failed for Captioning");
            MissingRequirements = [$"Readiness check failed: {ex.Message}"];
            Warnings = [];
            return false;
        }
    }

    /// <inheritdoc />
    public Task<CaptioningResult> GenerateSingleCaptionAsync(
        string imagePath,
        string prompt,
        string? triggerWord = null,
        IReadOnlyList<string>? blacklistedWords = null,
        float temperature = 0.7f,
        CancellationToken ct = default)
    {
        return GenerateSingleCaptionInternalAsync(imagePath, prompt, triggerWord, blacklistedWords, temperature, progress: null, ct);
    }

    private async Task<CaptioningResult> GenerateSingleCaptionInternalAsync(
        string imagePath,
        string prompt,
        string? triggerWord,
        IReadOnlyList<string>? blacklistedWords,
        float temperature,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (!File.Exists(imagePath))
        {
            return CaptioningResult.Failed(imagePath, "Image file not found.");
        }

        try
        {
            var rawCaption = await _comfyUi.GenerateCaptionAsync(imagePath, prompt, temperature, progress, ct);

            if (rawCaption is null)
            {
                return CaptioningResult.Failed(imagePath, "ComfyUI returned no caption text.");
            }

            var caption = CaptionPostProcessor.Process(rawCaption, triggerWord, blacklistedWords);
            return CaptioningResult.Succeeded(imagePath, caption, "");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ComfyUI captioning failed for {ImagePath}", imagePath);
            return CaptioningResult.Failed(imagePath, $"ComfyUI error: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaptioningResult>> GenerateBatchCaptionsAsync(
        CaptioningJobConfig config,
        IProgress<CaptioningProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var validationErrors = config.Validate();
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException($"Invalid configuration: {string.Join("; ", validationErrors)}");
        }

        var imagePaths = config.ImagePaths.ToList();
        var totalCount = imagePaths.Count;
        var results = new List<CaptioningResult>(totalCount);

        // Bridge WebSocket node-level progress to the batch progress reporter
        IProgress<string>? wsProgress = null;
        var currentWsImage = "";

        if (progress is not null)
        {
            wsProgress = new Progress<string>(nodeStatus =>
            {
                progress.Report(new CaptioningProgress(
                    results.Count, totalCount, currentWsImage,
                    nodeStatus));
            });
        }

        for (var i = 0; i < totalCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var imagePath = imagePaths[i];
            currentWsImage = imagePath;

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
                wsProgress,
                ct);

            // Save caption file if generation succeeded
            if (result.Success && result.Caption is not null)
            {
                try
                {
                    await File.WriteAllTextAsync(captionFilePath, result.Caption, ct);
                    result = CaptioningResult.Succeeded(imagePath, result.Caption, captionFilePath);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to save caption file {Path}", captionFilePath);
                    result = CaptioningResult.Failed(imagePath, $"Caption generated but failed to save: {ex.Message}");
                }
            }

            results.Add(result);

            progress?.Report(new CaptioningProgress(
                i + 1, totalCount, imagePath,
                $"Completed {i + 1}/{totalCount}: {Path.GetFileName(imagePath)}",
                result));
        }

        return results;
    }

    private static string GetCaptionFilePath(string imagePath, string? datasetPath)
    {
        var directory = datasetPath ?? Path.GetDirectoryName(imagePath) ?? ".";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
        return Path.Combine(directory, nameWithoutExt + ".txt");
    }
}
