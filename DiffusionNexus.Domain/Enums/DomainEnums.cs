namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Types of diffusion models.
/// </summary>
public enum ModelType
{
    Unknown = 0,
    Checkpoint,
    TextualInversion,
    Hypernetwork,
    AestheticGradient,
    LORA,
    LoCon,
    DoRA,
    Controlnet,
    Poses,
    Upscaler,
    MotionModule,
    VAE,
    Wildcards,
    Workflows,
    Other,
    Embedding,
    Detection,
    Motion,
    /// <summary>
    /// CLIP / T5 / LLM text encoders. Not a Civitai model type: Civitai has no such category, so
    /// this member only ever arrives from our own classification of a local file. Appended last —
    /// the value is persisted as a string (ModelConfiguration.cs:24), so appending is free and
    /// reordering would silently repoint every existing row.
    /// </summary>
    TextEncoder
}

/// <summary>
/// Categories for Civitai models based on their primary use case.
/// </summary>
public enum CivitaiCategory
{
    Unknown = 0,
    Character,
    Style,
    Celebrity,
    Concept,
    Clothing,
    BaseModel,
    Poses,
    Background,
    Tool,
    Buildings,
    Vehicle,
    Objects,
    Animal,
    Assets,
    Action
}

/// <summary>
/// Model availability mode.
/// </summary>
public enum ModelMode
{
    Available = 0,
    Archived,
    TakenDown
}

/// <summary>
/// File format types.
/// </summary>
public enum FileFormat
{
    Unknown = 0,
    SafeTensor,
    PickleTensor,
    Diffusers,
    Core,
    ONNX,
    Other
}

/// <summary>
/// File precision.
/// </summary>
public enum FilePrecision
{
    Unknown = 0,
    FP16,
    FP32,
    BF16
}

/// <summary>
/// File size type.
/// </summary>
public enum FileSizeType
{
    Unknown = 0,
    Full,
    Pruned
}

/// <summary>
/// Security scan result.
/// </summary>
public enum ScanResult
{
    Pending = 0,
    Success,
    Danger,
    Error
}

/// <summary>
/// NSFW content level.
/// </summary>
public enum NsfwLevel
{
    None = 0,
    Soft,
    Mature,
    X
}

/// <summary>
/// Commercial use permissions.
/// </summary>
public enum CommercialUse
{
    None = 0,
    Image,
    Rent,
    Sell
}

/// <summary>
/// Metadata completeness level.
/// </summary>
public enum MetadataStatus
{
    None = 0,
    Partial,
    Full
}

/// <summary>
/// Source of the model data.
/// </summary>
public enum DataSource
{
    Unknown = 0,
    LocalFile,
    CivitaiApi,
    Manual
}

/// <summary>
/// Supported installer types for diffusion interfaces.
/// </summary>
public enum InstallerType
{
    Unknown = 0,
    Automatic1111,
    Forge,
    ComfyUI,
    Fooocus,
    InvokeAI,
    FluxGym,
    SwarmUI,
    AIToolkit,
    /// <summary>
    /// Built-in pseudo-installer representing the in-process Diffusion Nexus Core
    /// capabilities (image captioning today, future embedding/translation backends).
    /// Cards of this type have no Launch action — only a Workloads button that
    /// downloads the supporting GGUF + mmproj files.
    /// </summary>
    DiffusionNexusCore
}
