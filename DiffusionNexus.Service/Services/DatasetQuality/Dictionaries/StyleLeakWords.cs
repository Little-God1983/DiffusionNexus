namespace DiffusionNexus.Service.Services.DatasetQuality.Dictionaries;

/// <summary>
/// Terms that describe artistic style, rendering technique, or aesthetic quality.
/// These words should typically NOT appear in Style LoRA captions because they
/// cause the model to associate specific content with the style, leading to
/// "style leakage" where the style only activates alongside those terms.
///
/// Also used by other checks to flag quality/meta tokens in Character and
/// Concept datasets (where they dilute the actual content description).
/// </summary>
public static class StyleLeakWords
{
    /// <summary>
    /// Case-insensitive set of terms that describe artistic style or quality.
    /// </summary>
    public static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        // Art style descriptors
        "anime", "anime style", "manga", "manga style",
        "cartoon", "cartoon style", "comic", "comic style",
        "realistic", "photorealistic", "hyper-realistic", "hyperrealistic",
        "semi-realistic", "illustration", "illustrated",
        "painting", "oil painting", "watercolor", "watercolour",
        "digital art", "digital painting", "digital illustration",
        "concept art", "pixel art", "vector art",
        "3d render", "3d rendering", "cgi", "3d",
        "sketch", "pencil drawing", "line art", "lineart",
        "cel shading", "cel-shaded", "flat color", "flat colour",

        // Aesthetic / quality meta tags (booru-style)
        "masterpiece", "best quality", "high quality", "highres",
        "high resolution", "absurdres", "incredibly absurdres",
        "hq", "uhd", "4k", "8k",
        "detailed", "highly detailed", "ultra detailed", "extremely detailed",
        "intricate", "intricate details",
        "sharp focus", "sharp", "crisp",
        "professional", "award-winning", "stunning",

        // Negative quality tags (commonly seen as anti-leak)
        "low quality", "worst quality", "lowres", "blurry",
        "jpeg artifacts", "watermark", "signature", "username",
        "artist name", "logo",

        // Rendering / lighting style
        "volumetric lighting", "volumetric", "ray tracing", "raytracing",
        "global illumination", "ambient occlusion",
        "cinematic", "cinematic lighting", "dramatic lighting",
        "film grain", "chromatic aberration", "lens flare",
        "bloom", "glow", "soft glow",
        "tilt shift", "motion blur", "depth of field",
        "bokeh", "vignette",

        // Art movement / period
        "art nouveau", "art deco", "baroque", "renaissance",
        "impressionism", "impressionist", "expressionism", "expressionist",
        "surrealism", "surrealist", "cubism", "cubist",
        "pop art", "minimalism", "minimalist",
        "gothic", "retro", "vintage", "noir",
        "cyberpunk", "steampunk", "vaporwave",
        "ukiyo-e", "ukiyoe",

        // Medium / texture
        "acrylic", "gouache", "pastel", "charcoal",
        "ink", "ink wash", "pen and ink",
        "colored pencil", "crayon", "marker",
        "impasto", "stippling", "crosshatch", "hatching",
        "textured", "smooth", "glossy", "matte",

        // Composition style
        "wide angle", "fisheye", "panoramic",
        "dutch angle", "symmetrical", "asymmetrical",
        "rule of thirds", "centered composition",
        "negative space", "busy background", "simple background",
        "white background", "black background", "gradient background",

        // AI / generation meta
        "stable diffusion", "midjourney", "dalle", "dall-e",
        "ai generated", "ai-generated", "generated",
        "nai", "novel ai", "novelai"
    };

    /// <summary>
    /// Subset of style leak words that are especially problematic in Style LoRA
    /// captions because they become entangled with the style itself.
    /// Flagged at Critical severity.
    /// </summary>
    public static readonly HashSet<string> CriticalForStyle = new(StringComparer.OrdinalIgnoreCase)
    {
        "masterpiece", "best quality", "high quality", "highres",
        "absurdres", "incredibly absurdres",
        "detailed", "highly detailed", "ultra detailed",
        "anime style", "manga style", "cartoon style", "comic style",
        "realistic", "photorealistic", "illustration",
        "digital art", "digital painting", "concept art",
        "oil painting", "watercolor", "sketch",
        "stable diffusion", "midjourney", "dalle", "ai generated"
    };
}
