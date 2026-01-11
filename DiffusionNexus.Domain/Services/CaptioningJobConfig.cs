using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Configuration for an AI image captioning job.
/// </summary>
/// <param name="ImagePaths">List of image file paths to process.</param>
/// <param name="SelectedModel">The vision-language model to use for captioning.</param>
/// <param name="SystemPrompt">The system prompt to guide caption generation. Default: "Describe the image using 100 English words".</param>
/// <param name="TriggerWord">Optional token to prepend to each generated caption.</param>
/// <param name="BlacklistedWords">Words to filter out of the generated captions.</param>
/// <param name="DatasetPath">Output directory for caption files.</param>
/// <param name="OverrideExisting">Whether to overwrite existing caption (.txt) files.</param>
/// <param name="Temperature">Inference creativity parameter (0.0-2.0). Lower values are more deterministic.</param>
public record CaptioningJobConfig(
    IEnumerable<string> ImagePaths,
    CaptioningModelType SelectedModel,
    string SystemPrompt = "Describe the image using 100 English words",
    string? TriggerWord = null,
    IReadOnlyList<string>? BlacklistedWords = null,
    string? DatasetPath = null,
    bool OverrideExisting = false,
    float Temperature = 0.7f)
{
    /// <summary>
    /// Validates the configuration.
    /// </summary>
    /// <returns>A list of validation error messages, empty if valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (ImagePaths is null || !ImagePaths.Any())
        {
            errors.Add("At least one image path is required.");
        }

        if (string.IsNullOrWhiteSpace(SystemPrompt))
        {
            errors.Add("System prompt cannot be empty.");
        }

        if (Temperature is < 0f or > 2f)
        {
            errors.Add("Temperature must be between 0.0 and 2.0.");
        }

        return errors;
    }
}
