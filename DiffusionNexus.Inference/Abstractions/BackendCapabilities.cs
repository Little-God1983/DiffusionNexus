namespace DiffusionNexus.Inference.Abstractions;

/// <summary>
/// A thing a generation UI can offer, which a backend may or may not honour.
/// </summary>
/// <remarks>
/// This exists because the two shipped backends disagree on most of the request: the local
/// stable-diffusion.cpp backend honours the sampler and LoRAs but the ComfyUI engine bakes them into
/// its workflow graph, while the engine honours the negative prompt. A control wired to a field the
/// selected backend drops is worse than a missing control — the user changes it, the image does not
/// change, and the model gets the blame.
/// </remarks>
public enum BackendFeature
{
    /// <summary>The request's <c>NegativePrompt</c> reaches the sampler.</summary>
    NegativePrompt,

    /// <summary>The request's <c>Sampler</c> and <c>Scheduler</c> choose the sampling algorithm.</summary>
    SamplerSelection,

    /// <summary>The request's <c>Steps</c> and <c>Cfg</c> override the model's defaults.</summary>
    StepsAndGuidance,

    /// <summary>The request's <c>Loras</c> are loaded over the base model.</summary>
    Loras,

    /// <summary>The request's <c>ControlNets</c> condition the generation.</summary>
    ControlNet,

    /// <summary>The request's <c>MaskImage</c> restricts repainting to the masked area.</summary>
    Inpainting,

    /// <summary>Cancellation stops the sampler mid-image rather than at the next phase boundary.</summary>
    MidSampleInterrupt,
}

/// <summary>
/// What a backend actually honours, and — for what it does not — a sentence a UI can put in front of
/// the user at the control itself.
/// </summary>
/// <remarks>
/// The reason lives here rather than in the UI because the backend is the thing that knows why. Issue
/// #518 states the rule this type serves: an unsupported control stays <b>visible and explained</b>,
/// because hiding it teaches nothing and greying it without a reason reads as a bug. Limitation text
/// should therefore name the backend that would honour the feature, so the sentence carries the way out.
/// </remarks>
public sealed class BackendCapabilities
{
    private readonly IReadOnlyDictionary<BackendFeature, string> _limitations;

    /// <summary>
    /// Creates a capability set.
    /// </summary>
    /// <param name="limitations">
    /// One entry per <b>unsupported</b> feature, mapping it to the one-line reason shown at the control.
    /// Anything absent from this map is supported. Stating it negatively keeps a newly added
    /// <see cref="BackendFeature"/> supported-by-default rather than silently disabling it everywhere,
    /// which would be the wrong failure direction: a feature that works but is greyed out is invisible,
    /// while one that is offered and does nothing is at least reportable.
    /// </param>
    public BackendCapabilities(IReadOnlyDictionary<BackendFeature, string>? limitations = null)
    {
        _limitations = limitations ?? new Dictionary<BackendFeature, string>();
    }

    /// <summary>A backend that honours everything. Useful for tests and for fully capable adapters.</summary>
    public static BackendCapabilities All { get; } = new();

    /// <summary>Whether <paramref name="feature"/> reaches the model.</summary>
    public bool Supports(BackendFeature feature) => !_limitations.ContainsKey(feature);

    /// <summary>
    /// The one-line reason <paramref name="feature"/> is unavailable, or null when it is supported.
    /// </summary>
    public string? LimitationFor(BackendFeature feature) =>
        _limitations.TryGetValue(feature, out var reason) ? reason : null;
}
