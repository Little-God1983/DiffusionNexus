namespace DiffusionNexus.Inference.Abstractions;

/// <summary>
/// Phase of a diffusion run. Reported via the streaming progress channel so the UI can
/// distinguish "loading the 12 GB model" from "actively sampling step 5/9".
/// </summary>
public enum DiffusionPhase
{
    /// <summary>The backend is loading model weights (cold start). Progress is indeterminate.</summary>
    Loading,

    /// <summary>The backend is encoding the text prompt(s).</summary>
    Encoding,

    /// <summary>The backend is in the sampling loop. <c>Step</c>/<c>TotalSteps</c> are meaningful.</summary>
    Sampling,

    /// <summary>The backend is decoding the latent through the VAE.</summary>
    Decoding,

    /// <summary>Generation finished; the next message will be the final result.</summary>
    Completed,
}

/// <summary>
/// One progress notification from a running generation.
/// </summary>
public sealed class DiffusionProgress
{
    /// <summary>Current pipeline phase.</summary>
    public required DiffusionPhase Phase { get; init; }

    /// <summary>Current sampling step (1-based). 0 when not in <see cref="DiffusionPhase.Sampling"/>.</summary>
    public int Step { get; init; }

    /// <summary>Total sampling steps. 0 when not in <see cref="DiffusionPhase.Sampling"/>.</summary>
    public int TotalSteps { get; init; }

    /// <summary>Iterations per second reported by the native engine.</summary>
    public double IterationsPerSecond { get; init; }

    /// <summary>Optional human-readable status text (e.g. "Loading Z-Image-Turbo…").</summary>
    public string? Message { get; init; }

    // TODO(v2-live-preview): Backend currently always sets this to null. When upstream
    // stable-diffusion.cpp exposes an intermediate-latent callback, decode it to a
    // PNG byte[] here so the UI can show step-by-step previews on the canvas.
    /// <summary>Optional preview image for the current step. <b>Always null in v1.</b></summary>
    public byte[]? PreviewPngBytes { get; init; }
}
