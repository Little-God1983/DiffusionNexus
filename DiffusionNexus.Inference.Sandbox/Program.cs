// FLUX.2-klein Anime-To-Real smoke test — REFERENCE-IMAGE path (matches the ComfyUI workflow:
// empty latent + input image as a Flux.2 reference in conditioning, NOT classic img2img/denoise).
// Proves: (1) SDNet RefImages API works for flux2-klein, (2) WithRefImageAutoResize avoids the
// size-mismatch native crash (output size 1216x832 deliberately != reference image size).

using HPPH.SkiaSharp;
using StableDiffusion.NET;

const string Unet = @"D:\Models\DiffusionModels\flux-2-klein-9b-BF16.gguf";
const string Llm  = @"D:\Models\TextEncoders\Qwen3-8B-Q4_K_M.gguf";
const string Vae  = @"D:\Models\vae\flux2-vae.safetensors";

StableDiffusionCpp.InitializeEvents();
StableDiffusionCpp.Log += (_, a) => Console.WriteLine($"[NATIVE {a.Level}] {a.Text?.TrimEnd()}");
StableDiffusionCpp.Progress += (_, a) => Console.WriteLine($"  step {a.Step}/{a.Steps} ({a.Progress * 100:N0}%)");

Console.WriteLine($"SD.NET commit: {StableDiffusionCpp.GetSDCommit()}  version: {StableDiffusionCpp.GetSDVersion()}");

var parameters = DiffusionModelParameter.Create()
    .WithDiffusionModelPath(Unet)
    .WithLLMPath(Llm)
    .WithVae(Vae)
    .WithPrediction(Prediction.Flux2Flow)
    .WithVaeTiling()
    .WithMultithreading()
    .WithFlashAttention();

Console.WriteLine("=== Loading FLUX.2-klein ===");
DiffusionModel model;
try
{
    model = new DiffusionModel(parameters);
    Console.WriteLine(">>> MODEL LOADED OK");
}
catch (Exception ex)
{
    Console.WriteLine($">>> LOAD FAILED: {ex.GetType().Name}: {ex.Message}\n{ex}");
    return 1;
}

try
{
    const string RefImage = @"D:\Models\flux2-klein-smoketest.png";
    const string Lora1 = @"D:\Models\Lora\Flux2\A2R_Klein_Standard.safetensors";
    const string Lora2 = @"D:\Models\Lora\Sort\flux220kleinE58AA8E6BCABE8BDACE5.O9j8.safetensors";

    Console.WriteLine("=== REFERENCE-IMAGE generation: TextToImage + RefImages + AutoResize, 1216x832, 8 steps, cfg 1, 2 LoRAs @0.75 ===");
    Console.WriteLine($"  ref  : {(File.Exists(RefImage) ? "OK" : "MISSING")} {RefImage}");

    var refImage = ImageHelper.LoadImage(RefImage);

    var genParams = ImageGenerationParameter.TextToImage("Transform this into a photo, Realistic style")
        .WithSize(1216, 832)   // deliberately != reference size -> exercises AutoResize
        .WithSteps(8)
        .WithCfg(1.0f)
        .WithSampler(Sampler.Euler)
        .WithRefImageAutoResize(true);

    genParams.RefImages = new[] { refImage };
    if (File.Exists(Lora1)) genParams.Loras.Add(new Lora(Lora1) { Multiplier = 0.75f });
    if (File.Exists(Lora2)) genParams.Loras.Add(new Lora(Lora2) { Multiplier = 0.75f });

    Console.WriteLine($"  RefImages.Count = {genParams.RefImages.Length}, Loras.Count = {genParams.Loras.Count}");

    var image = model.GenerateImage(genParams);

    var outPath = @"D:\Models\flux2-refimage-smoketest.png";
    File.WriteAllBytes(outPath, image.ToPng());
    Console.WriteLine($">>> GENERATED OK ({image.Width}x{image.Height}) -> {outPath}");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($">>> GENERATE FAILED: {ex.GetType().Name}: {ex.Message}\n{ex}");
    return 2;
}
finally
{
    model.Dispose();
}
