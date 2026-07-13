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
}
