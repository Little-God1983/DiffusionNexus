namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// Lifecycle state of a single <see cref="GenerationFrameViewModel"/>.
/// Drives which overlay (idle / loading / sampling / done / error) the frame view shows.
/// </summary>
public enum GenerationFrameState
{
    /// <summary>Empty frame waiting for the user to press Generate.</summary>
    Idle,

    /// <summary>Backend is loading the model (cold start) or otherwise warming up.</summary>
    Loading,

    /// <summary>Backend is in the sampling loop. <c>StepCurrent</c>/<c>StepTotal</c> are meaningful.</summary>
    Sampling,

    /// <summary>Generation completed successfully and <c>ImagePath</c> points at the saved file.</summary>
    Completed,

    /// <summary>Generation failed; <c>StatusText</c> carries the error message.</summary>
    Failed,
}
