using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Models.Distiller;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>One image to distill: source path, parsed+edited data, curated prompts, included LoRAs.</summary>
internal sealed record DistillItem(string SourcePath, ImageGenerationData Data, string Positive, string? Negative, IReadOnlyList<LoraInfo> Loras);

/// <summary>A per-item failure captured during a run (the batch continues past it).</summary>
internal sealed record DistillFailure(string SourcePath, string Error);

/// <summary>Outcome of a distill run.</summary>
internal sealed record DistillResult(int Written, IReadOnlyList<DistillFailure> Failures);

/// <summary>
/// Runs the distill pipeline over a batch: apply rule sets → format A1111 parameters (optionally with
/// resource hashes) → write a clean copy (optionally workflow-stripped) into the output folder. v1
/// writes PNG output only; non-PNG sources are reported as failures.
/// </summary>
internal sealed class MetadataDistillerService
{
    private readonly ImageResourceHasher _hasher;

    public MetadataDistillerService(ImageResourceHasher hasher) => _hasher = hasher;

    public async Task<DistillResult> DistillAsync(
        IReadOnlyList<DistillItem> items,
        IReadOnlyList<PromptRuleSet> ruleSets,
        DistillOptions options,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.OutputFolder))
            throw new InvalidOperationException("Output folder is not set.");
        Directory.CreateDirectory(options.OutputFolder);

        var failures = new List<DistillFailure>();
        int written = 0, done = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!string.Equals(Path.GetExtension(item.SourcePath), ".png", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Only PNG output is supported in this version.");

                var positive = PromptRuleEngine.Apply(item.Positive ?? "", ruleSets);
                var negativeRaw = item.Negative;
                var negative = string.IsNullOrWhiteSpace(negativeRaw) ? null : PromptRuleEngine.Apply(negativeRaw, ruleSets);

                ResourceHashes? hashes = null;
                if (options.ComputeHashes)
                    hashes = await _hasher.ComputeAsync(item.Data.Checkpoint, item.Loras, ct).ConfigureAwait(false);

                var parameters = A1111MetadataFormatter.Build(item.Data, positive, negative, item.Loras, hashes);

                var dest = UniquePath(options.OutputFolder!, Path.GetFileName(item.SourcePath));
                PngMetadataWriter.CopyWithMetadata(
                    item.SourcePath, dest,
                    new Dictionary<string, string> { ["parameters"] = parameters },
                    stripExisting: options.StripWorkflow);

                written++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failures.Add(new DistillFailure(item.SourcePath, ex.Message));
            }
            finally
            {
                progress?.Report(++done);
            }
        }

        return new DistillResult(written, failures);
    }

    private static string UniquePath(string folder, string fileName)
    {
        var dest = Path.Combine(folder, fileName);
        if (!File.Exists(dest)) return dest;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
