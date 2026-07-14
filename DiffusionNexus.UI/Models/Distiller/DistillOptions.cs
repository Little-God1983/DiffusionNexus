namespace DiffusionNexus.UI.Models.Distiller;

/// <summary>Run-time options for a distill run.</summary>
public sealed class DistillOptions
{
    /// <summary>Remove the embedded ComfyUI workflow/prompt chunks from output (default on).</summary>
    public bool StripWorkflow { get; set; } = true;

    /// <summary>Compute AutoV2 resource hashes for found LoRAs/checkpoints (slower).</summary>
    public bool ComputeHashes { get; set; }

    /// <summary>Destination folder for cleaned copies. Must be set before a run.</summary>
    public string? OutputFolder { get; set; }

    /// <summary>
    /// When set, output images larger than this on their longest side are downscaled to fit
    /// (aspect preserved). Null keeps the original resolution. Forces a re-encode.
    /// </summary>
    public int? ResizeMaxDimension { get; set; }

    /// <summary>
    /// Re-encode the PNG at maximum zlib compression for a smaller file (lossless, slower).
    /// When false and no resize is requested, pixels are copied byte-identical.
    /// </summary>
    public bool RecompressPng { get; set; }
}
