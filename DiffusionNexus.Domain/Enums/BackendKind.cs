namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Identifies the execution engine that a feature runs on.
/// </summary>
public enum BackendKind
{
    /// <summary>
    /// The feature is executed by a (typically local) ComfyUI server. Readiness is determined
    /// by the disk-based workload checker — same source the Installer Manager uses.
    /// </summary>
    ComfyUI,

    /// <summary>
    /// The feature is executed in-process via the local inference stack
    /// (LlamaSharp captioning, stable-diffusion.cpp generation, etc.). Readiness is determined
    /// by the relevant native library + model files being present.
    /// </summary>
    LocalInference
}
