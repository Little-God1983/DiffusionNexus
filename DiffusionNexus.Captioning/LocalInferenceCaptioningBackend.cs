using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Captioning;

/// <summary>
/// Captioning backend adapter that delegates to the local LlamaSharp-based <see cref="ICaptioningService"/>.
/// </summary>
public sealed class LocalInferenceCaptioningBackend : ICaptioningBackend
{
    private static readonly ILogger Logger = Log.ForContext<LocalInferenceCaptioningBackend>();

    private readonly ICaptioningService _captioningService;

    /// <summary>
    /// Creates a new local inference captioning backend.
    /// </summary>
    /// <param name="captioningService">The underlying LlamaSharp captioning service.</param>
    public LocalInferenceCaptioningBackend(ICaptioningService captioningService)
    {
        ArgumentNullException.ThrowIfNull(captioningService);
        _captioningService = captioningService;
    }

    /// <inheritdoc />
    public string DisplayName => "Local Inference (LlamaSharp)";

    /// <inheritdoc />
    public IReadOnlyList<string> MissingRequirements => [];

    /// <inheritdoc />
    public IReadOnlyList<string> Warnings => [];

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var available = _captioningService.IsNativeLibraryLoaded;
        return Task.FromResult(available);
    }

    /// <inheritdoc />
    public async Task<CaptioningResult> GenerateSingleCaptionAsync(
        string imagePath,
        string prompt,
        string? triggerWord = null,
        IReadOnlyList<string>? blacklistedWords = null,
        float temperature = 0.7f,
        CancellationToken ct = default)
    {
        return await _captioningService.GenerateSingleCaptionAsync(
            imagePath, prompt, triggerWord, blacklistedWords, temperature, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaptioningResult>> GenerateBatchCaptionsAsync(
        CaptioningJobConfig config,
        IProgress<CaptioningProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await _captioningService.GenerateCaptionsAsync(config, progress, ct);
    }
}
