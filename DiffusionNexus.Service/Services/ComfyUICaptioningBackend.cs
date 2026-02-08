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

    /// <summary>
    /// Custom node types required by the captioning workflow.
    /// Add new entries here when additional workflows introduce new node dependencies.
    /// </summary>
    private static readonly string[] RequiredCustomNodes = ["Qwen3_VQA", "ShowText|pysssss"];

    private readonly IComfyUIWrapperService _comfyUi;

    /// <summary>
    /// Creates a new ComfyUI captioning backend.
    /// </summary>
    /// <param name="comfyUiService">The ComfyUI wrapper service to delegate to.</param>
    public ComfyUICaptioningBackend(IComfyUIWrapperService comfyUiService)
    {
        ArgumentNullException.ThrowIfNull(comfyUiService);
        _comfyUi = comfyUiService;
    }

    /// <inheritdoc />
    public string DisplayName => "ComfyUI – Qwen3-VL";

    /// <inheritdoc />
    public IReadOnlyList<string> MissingRequirements { get; private set; } = [];

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            // Lightweight health check — is the server reachable?
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await httpClient.GetAsync(
                $"{GetBaseUrl()}/system_stats", ct);

            if (!response.IsSuccessStatusCode)
            {
                MissingRequirements = ["ComfyUI server is not reachable"];
                return false;
            }

            // Server is up — verify the required custom nodes are installed
            var missingNodes = await _comfyUi.CheckRequiredNodesAsync(RequiredCustomNodes, ct);

            if (missingNodes.Count > 0)
            {
                MissingRequirements = missingNodes
                    .Select(n => $"Missing custom node: {n}")
                    .ToList();

                Logger.Warning(
                    "ComfyUI server is reachable but missing required custom nodes: {MissingNodes}. " +
                    "Install them via ComfyUI Manager or manually into the custom_nodes folder",
                    string.Join(", ", missingNodes));
                return false;
            }

            MissingRequirements = [];
            return true;
        }
        catch
        {
            MissingRequirements = ["ComfyUI server is not reachable"];
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<CaptioningResult> GenerateSingleCaptionAsync(
        string imagePath,
        string prompt,
        string? triggerWord = null,
        IReadOnlyList<string>? blacklistedWords = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (!File.Exists(imagePath))
        {
            return CaptioningResult.Failed(imagePath, "Image file not found.");
        }

        try
        {
            var rawCaption = await _comfyUi.GenerateCaptionAsync(imagePath, prompt, ct: ct);

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

        for (var i = 0; i < totalCount; i++)
        {
            ct.ThrowIfCancellationRequested();

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

            var result = await GenerateSingleCaptionAsync(
                imagePath,
                config.SystemPrompt,
                config.TriggerWord,
                config.BlacklistedWords,
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

    private static string GetBaseUrl()
    {
        // Default ComfyUI URL — consistent with ComfyUIWrapperService default
        return "http://127.0.0.1:8188";
    }
}
