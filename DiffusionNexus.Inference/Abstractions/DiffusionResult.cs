namespace DiffusionNexus.Inference.Abstractions;

/// <summary>
/// Output of a successful diffusion run.
/// </summary>
/// <param name="PngBytes">PNG-encoded image bytes ready to write to disk or feed into <c>Bitmap</c>.</param>
/// <param name="Width">Final image width in pixels.</param>
/// <param name="Height">Final image height in pixels.</param>
/// <param name="Seed">The seed actually used (echoes <c>DiffusionRequest.Seed</c> when set, otherwise the chosen random seed).</param>
/// <param name="Duration">Wall-clock time spent inside <c>GenerateAsync</c> (model load excluded if context was cached).</param>
public sealed record DiffusionResult(
    byte[] PngBytes,
    int Width,
    int Height,
    long Seed,
    TimeSpan Duration);
