using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Services.Pipelines;

/// <inheritdoc />
public sealed class PipelineOutputWriter : IPipelineOutputWriter
{
    private readonly IDialogService _dialogs;
    private readonly IDatasetEventAggregator _events;

    public PipelineOutputWriter(IDialogService dialogs, IDatasetEventAggregator events)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    /// <inheritdoc />
    public async Task<PipelineOutputTarget?> PrepareAsync(
        PipelineOutputOption option, DatasetCardViewModel? dataset, CancellationToken cancellationToken = default)
    {
        switch (option.Mode)
        {
            case PipelineOutputMode.NewDatasetVersion:
                if (dataset is null)
                    throw new InvalidOperationException("Saving a new version requires a selected dataset.");

                // Reuse the app's versioning: creates the next V{n} folder, records the branch,
                // saves metadata and publishes VersionCreated so other views refresh.
                var newVersion = await DatasetVersionUtilities
                    .CreateEmptyVersionAsync(dataset, dataset.CurrentVersion, _events)
                    .ConfigureAwait(true);
                return new PipelineOutputTarget(option.Mode, dataset.GetVersionFolderPath(newVersion), dataset, newVersion);

            case PipelineOutputMode.PickFolder:
                var folder = await _dialogs.ShowOpenFolderDialogAsync("Select an output folder").ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(folder))
                    return null; // cancelled
                return new PipelineOutputTarget(option.Mode, folder, null, null);

            case PipelineOutputMode.InputFolderInPlace:
                return new PipelineOutputTarget(option.Mode, null, null, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(option));
        }
    }

    /// <inheritdoc />
    public async Task<string> WriteAsync(
        PipelineOutputTarget target, string inputPath, byte[] pngBytes, CancellationToken cancellationToken = default)
    {
        var directory = target.FixedDirectory ?? Path.GetDirectoryName(inputPath)
            ?? throw new InvalidOperationException($"Cannot resolve an output directory for '{inputPath}'.");
        Directory.CreateDirectory(directory);

        var baseName = Path.GetFileNameWithoutExtension(inputPath);

        // In-place must not clobber the source; the other modes write into a dedicated folder so the
        // original name is preserved (keeps a dataset version aligned with its source filenames).
        var stem = target.Mode == PipelineOutputMode.InputFolderInPlace
            ? $"{baseName}_real"
            : baseName;

        // Never overwrite: if the name is taken, fall back to "-2", "-3", … (e.g. re-running the
        // same input into the same folder).
        var outputPath = MakeUniquePath(directory, stem, ".png");
        await File.WriteAllBytesAsync(outputPath, pngBytes, cancellationToken).ConfigureAwait(false);

        // For a new dataset version, carry over the sibling caption so the version stays a complete
        // dataset (named to match the actual output file, which may have a "-N" suffix).
        if (target.Mode == PipelineOutputMode.NewDatasetVersion)
        {
            var srcCaption = Path.ChangeExtension(inputPath, ".txt");
            if (File.Exists(srcCaption))
            {
                var dstCaption = Path.Combine(directory, Path.GetFileNameWithoutExtension(outputPath) + ".txt");
                try { File.Copy(srcCaption, dstCaption, overwrite: false); } catch { /* non-fatal */ }
            }
        }

        return outputPath;
    }

    /// <summary>
    /// Returns a path that does not yet exist: <c>{stem}{ext}</c>, then <c>{stem}-2{ext}</c>,
    /// <c>{stem}-3{ext}</c>, … so an output never overwrites an existing file.
    /// </summary>
    private static string MakeUniquePath(string directory, string stem, string extension)
    {
        var candidate = Path.Combine(directory, stem + extension);
        if (!File.Exists(candidate))
            return candidate;

        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(directory, $"{stem}-{n}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
