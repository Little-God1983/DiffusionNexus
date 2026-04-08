using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Contract for a single, self-contained dataset quality check.
/// Each implementation examines one aspect of the dataset (e.g. trigger-word
/// presence, tag consistency, image resolution) and returns a list of issues.
///
/// <para>
/// New checks are added by implementing this interface — the pipeline
/// discovers and runs them automatically without touching existing code.
/// </para>
/// </summary>
public interface IDatasetCheck
{
    /// <summary>
    /// Human-readable name shown in the UI and included on every <see cref="Issue"/>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Short description of what this check validates.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The domain this check belongs to (Caption or Image).
    /// </summary>
    CheckDomain Domain { get; }

    /// <summary>
    /// Execution order within the pipeline. Lower values run first.
    /// Checks with the same order run in registration order.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Determines whether this check should run for the given LoRA type.
    /// For example, a "trigger word in every caption" check may only apply
    /// to Character LoRAs.
    /// </summary>
    /// <param name="loraType">The LoRA type of the dataset being analyzed.</param>
    /// <returns>True if this check should be included in the pipeline run.</returns>
    bool IsApplicable(LoraType loraType);

    /// <summary>
    /// Executes the check against the loaded caption files and dataset configuration.
    /// </summary>
    /// <param name="captions">All caption files loaded from the dataset folder.</param>
    /// <param name="config">Dataset configuration (folder path, trigger word, LoRA type).</param>
    /// <returns>A list of issues found. Empty list means the check passed.</returns>
    List<Issue> Run(IReadOnlyList<CaptionFile> captions, DatasetConfig config);

    /// <summary>
    /// Executes the check with per-item progress reporting.
    /// Implementations that process captions one-by-one can override this
    /// to report their current item index via <paramref name="itemProgress"/>.
    /// The default implementation delegates to <see cref="Run(IReadOnlyList{CaptionFile}, DatasetConfig)"/>.
    /// </summary>
    /// <param name="captions">All caption files loaded from the dataset folder.</param>
    /// <param name="config">Dataset configuration (folder path, trigger word, LoRA type).</param>
    /// <param name="itemProgress">Reports the 1-based index of the caption currently being processed.</param>
    /// <returns>A list of issues found. Empty list means the check passed.</returns>
    List<Issue> Run(IReadOnlyList<CaptionFile> captions, DatasetConfig config, IProgress<int>? itemProgress)
        => Run(captions, config);
}
