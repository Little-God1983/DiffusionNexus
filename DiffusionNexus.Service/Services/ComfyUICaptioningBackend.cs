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

    /// <summary>
    /// The model name used in the Qwen3-VL captioning workflow.
    /// Must match the <c>model</c> input value in <c>Qwen-3VL-autocaption.json</c>.
    /// </summary>
    private const string RequiredModelName = "Qwen3-VL-4B-Instruct-FP8";

    /// <summary>
    /// The node class_type whose <c>model</c> input we query via <c>/object_info</c>
    /// to get the authoritative list of available models.
    /// </summary>
    private const string Qwen3VqaNodeType = "Qwen3_VQA";

    /// <summary>
    /// The input name on the <see cref="Qwen3VqaNodeType"/> node that selects the model.
    /// </summary>
    private const string ModelInputName = "model";

    private readonly IComfyUIWrapperService _comfyUi;
    private readonly IAppSettingsService _settingsService;

    /// <summary>
    /// Creates a new ComfyUI captioning backend.
    /// </summary>
    /// <param name="comfyUiService">The ComfyUI wrapper service to delegate to.</param>
    /// <param name="settingsService">Application settings service for reading the configured server URL.</param>
    public ComfyUICaptioningBackend(IComfyUIWrapperService comfyUiService, IAppSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(comfyUiService);
        ArgumentNullException.ThrowIfNull(settingsService);
        _comfyUi = comfyUiService;
        _settingsService = settingsService;
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
        try
        {
            var serverUrl = await GetConfiguredUrlAsync(ct);

            // Lightweight health check — is the server reachable?
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await httpClient.GetAsync(
                $"{serverUrl}/system_stats", ct);

            if (!response.IsSuccessStatusCode)
            {
                MissingRequirements = [$"ComfyUI server not reachable at {serverUrl}"];
                Warnings = [];
                return false;
            }

            // Server is up — verify the required custom nodes are installed
            var missingNodes = await _comfyUi.CheckRequiredNodesAsync(RequiredCustomNodes, ct);

            if (missingNodes.Count > 0)
            {
                var items = missingNodes
                    .Select(n => $"Missing custom node: {n}")
                    .ToList();
                items.Add("Install via ComfyUI Manager or place into the custom_nodes folder, then restart ComfyUI.");
                MissingRequirements = items;
                Warnings = [];

                Logger.Warning(
                    "ComfyUI server is reachable but missing required custom nodes: {MissingNodes}. " +
                    "Install them via ComfyUI Manager or manually into the custom_nodes folder",
                    string.Join(", ", missingNodes));
                return false;
            }

            MissingRequirements = [];

            // Authoritative model check: query /object_info for the Qwen3_VQA node's
            // "model" input options — the exact values ComfyUI shows in its dropdown.
            var availableModels = await _comfyUi.GetNodeInputOptionsAsync(
                Qwen3VqaNodeType, ModelInputName, ct);

            Logger.Debug(
                "Qwen3_VQA node reports {Count} available model(s): {Models}",
                availableModels.Count,
                string.Join(", ", availableModels));

            if (!availableModels.Any(m => m.Contains(RequiredModelName, StringComparison.OrdinalIgnoreCase)))
            {
                Warnings = [$"The model '{RequiredModelName}' is not yet downloaded. The first run will automatically download it (~8 GB). This may take several minutes."];
            }
            else
            {
                Warnings = [];
            }

            return true;
        }
        catch (Exception ex)
        {
            var url = "(unknown)";
            try { url = await GetConfiguredUrlAsync(CancellationToken.None); }
            catch { /* best-effort */ }

            Logger.Debug(ex, "ComfyUI availability check failed for {ServerUrl}", url);
            MissingRequirements = [$"ComfyUI server not reachable at {url}"];
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

    private async Task<string> GetConfiguredUrlAsync(CancellationToken ct)
    {
        var settings = await _settingsService.GetSettingsAsync(ct);
        return settings.ComfyUiServerUrl.TrimEnd('/');
    }
}
