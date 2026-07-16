using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Services.Pipelines;

/// <summary>A resolved output destination for one pipeline run (prepared once before the batch).</summary>
/// <param name="Mode">The destination kind.</param>
/// <param name="FixedDirectory">The single output folder (new version / picked folder); null for in-place.</param>
/// <param name="Dataset">The dataset a new version was created under, if any.</param>
/// <param name="NewVersion">The created version number, if any.</param>
public sealed record PipelineOutputTarget(
    PipelineOutputMode Mode, string? FixedDirectory, DatasetCardViewModel? Dataset, int? NewVersion);

/// <summary>
/// Resolves and writes pipeline output: a new dataset version, a user-picked folder, or in-place
/// next to each source image. Reuses the dataset versioning machinery so "save as a new version"
/// behaves exactly like the rest of the app.
/// </summary>
public interface IPipelineOutputWriter
{
    /// <summary>
    /// Prepares the output destination before the batch. For NewDatasetVersion this creates the
    /// empty version folder (and publishes VersionCreated); for PickFolder it shows the folder
    /// picker. Returns null when the user cancels (e.g. closes the folder picker).
    /// </summary>
    Task<PipelineOutputTarget?> PrepareAsync(
        PipelineOutputOption option, DatasetCardViewModel? dataset, CancellationToken cancellationToken = default);

    /// <summary>Writes one generated PNG for the given input image and returns the output path.</summary>
    Task<string> WriteAsync(
        PipelineOutputTarget target, string inputPath, byte[] pngBytes, CancellationToken cancellationToken = default);
}
