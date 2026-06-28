using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Services.Lora;

/// <summary>One installed LoRA available to pick, sourced from the same library the LoRA Viewer scans.</summary>
/// <param name="DisplayName">Human-readable model name (falls back to the file name).</param>
/// <param name="FilePath">Absolute path to the LoRA weights file on disk.</param>
/// <param name="BaseModel">The raw Civitai base-model string (e.g. "Flux.2 Klein 9B"), or null if unknown.</param>
public sealed record AvailableLora(string DisplayName, string FilePath, string? BaseModel);

/// <summary>
/// Lists installed LoRAs for the reusable Multi-LoRA Picker, drawing from the SAME sources the
/// "LoRA Viewer → Installed" tab uses (the enabled <c>LoraSources</c> configured in Settings, via the
/// model database), optionally filtered to one or more base models.
/// </summary>
public interface ILoraCatalog
{
    /// <summary>
    /// Returns the installed LoRAs whose base model matches one of <paramref name="baseModelFilter"/>
    /// (case-insensitive, OR semantics). A null/empty filter returns all installed LoRAs. LoRAs whose
    /// base model is unknown ("???"/null) are excluded when a filter is supplied.
    /// </summary>
    Task<IReadOnlyList<AvailableLora>> GetInstalledLorasAsync(
        IReadOnlyCollection<string>? baseModelFilter,
        CancellationToken cancellationToken = default);
}
