using System.Text.Json.Serialization;

namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// Model types supported by Civitai.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitaiModelType
{
    Unknown = 0,
    Checkpoint,
    TextualInversion,
    Hypernetwork,
    AestheticGradient,
    LORA,
    Controlnet,
    Poses,
    LoCon,
    DoRA,
    Upscaler,
    MotionModule,
    VAE,
    Wildcards,
    Workflows,
    Other
}

/// <summary>
/// Model mode indicating availability.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitaiModelMode
{
    None = 0,
    Archived,
    TakenDown
}

/// <summary>
/// Base model the resource is trained for.
/// </summary>
public static class CivitaiBaseModel
{
    public const string SD14 = "SD 1.4";
    public const string SD15 = "SD 1.5";
    public const string SD15LCM = "SD 1.5 LCM";
    public const string SD20 = "SD 2.0";
    public const string SD21 = "SD 2.1";
    public const string SDXL09 = "SDXL 0.9";
    public const string SDXL10 = "SDXL 1.0";
    public const string SDXLTurbo = "SDXL Turbo";
    public const string SDXLLightning = "SDXL Lightning";
    public const string SDXLDistilled = "SDXL Distilled";
    public const string SD3 = "SD 3";
    public const string SD35 = "SD 3.5";
    public const string SD35Large = "SD 3.5 Large";
    public const string SD35Medium = "SD 3.5 Medium";
    public const string Flux1D = "Flux.1 D";
    public const string Flux1S = "Flux.1 S";
    public const string Pony = "Pony";
    public const string Illustrious = "Illustrious";
    public const string NoobAI = "NoobAI";
    public const string Hunyuan = "Hunyuan";
    public const string HunyuanVideo = "Hunyuan Video";
    public const string WanVideo21 = "Wan Video 2.1";
    public const string WanVideo22 = "Wan Video 2.2";
    public const string Other = "Other";
}

/// <summary>
/// File scan result status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitaiScanResult
{
    Pending,
    Success,
    Danger,
    Error
}

/// <summary>
/// File floating point precision.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitaiFloatingPoint
{
    fp16,
    fp32,
    bf16
}

/// <summary>
/// File size type.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitaiFileSize
{
    full,
    pruned
}

/// <summary>
/// File format type.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitaiFileFormat
{
    SafeTensor,
    PickleTensor,
    Diffusers,
    Core,
    ONNX,
    Other
}

/// <summary>
/// NSFW level classification.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitaiNsfwLevel
{
    None,
    Soft,
    Mature,
    X
}

/// <summary>
/// Commercial use permissions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitaiCommercialUse
{
    None,
    Image,
    Rent,
    Sell
}

/// <summary>
/// Sort options for model queries.
/// </summary>
public static class CivitaiModelSort
{
    public const string HighestRated = "Highest Rated";
    public const string MostDownloaded = "Most Downloaded";
    public const string Newest = "Newest";
}

/// <summary>
/// Time period for sorting.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitaiPeriod
{
    AllTime,
    Year,
    Month,
    Week,
    Day
}
