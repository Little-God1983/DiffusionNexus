// FLUX.2-klein load/generate smoke test.
// Reproduces the canvas failure ("Failed to initialize diffusion-model.") headlessly and prints the
// native stable-diffusion.cpp log — the only place that explains *why* the model fails to load.
// Uses the EXACT same parameters as StableDiffusionCppLoader.BuildFlux2Klein.

using HPPH;
using HPPH.SkiaSharp;
using StableDiffusion.NET;

const string Unet = @"D:\Models\DiffusionModels\flux-2-klein-9b-BF16.gguf";
const string Llm  = @"D:\Models\TextEncoders\Qwen3-8B-Q4_K_M.gguf";
const string Vae  = @"D:\Models\vae\flux2-vae.safetensors";

StableDiffusionCpp.InitializeEvents();
StableDiffusionCpp.Log += (_, a) => Console.WriteLine($"[NATIVE {a.Level}] {a.Text?.TrimEnd()}");
StableDiffusionCpp.Progress += (_, a) => Console.WriteLine($"  step {a.Step}/{a.Steps} ({a.Progress * 100:N0}%)");

Console.WriteLine($"SD.NET commit: {StableDiffusionCpp.GetSDCommit()}  version: {StableDiffusionCpp.GetSDVersion()}");
foreach (var (label, path) in new[] { ("UNET", Unet), ("LLM", Llm), ("VAE", Vae) })
{
    var info = File.Exists(path) ? $"OK  {new FileInfo(path).Length / (1024 * 1024)} MB" : "MISSING";
    Console.WriteLine($"{label,-5}: {info}  {path}");
}

Console.WriteLine();
Console.WriteLine("=== Building FLUX.2-klein parameters (WithDiffusionModelPath + WithLLMPath + WithVae + Prediction.Flux2Flow) ===");
var parameters = DiffusionModelParameter.Create()
    .WithDiffusionModelPath(Unet)
    .WithLLMPath(Llm)
    .WithVae(Vae)
    .WithPrediction(Prediction.Flux2Flow)
    .WithVaeTiling()
    .WithMultithreading()
    .WithFlashAttention();

Console.WriteLine("=== Loading model (new DiffusionModel) ===");
DiffusionModel model;
try
{
    model = new DiffusionModel(parameters);
    Console.WriteLine(">>> MODEL LOADED OK");
}
catch (Exception ex)
{
    Console.WriteLine($">>> LOAD FAILED: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex);
    return 1;
}

try
{
    // img2img smoke test (anime->real path): init image + denoise strength + the 2 anime-to-real LoRAs.
    const string InitImage = @"D:\Models\flux2-klein-smoketest.png"; // any existing image as the init
    const string Lora1 = @"D:\Models\Lora\Flux2\A2R_Klein_Standard.safetensors";            // modelId 1934100
    const string Lora2 = @"D:\Models\Lora\Sort\flux220kleinE58AA8E6BCABE8BDACE5.O9j8.safetensors"; // modelId 2343188

    Console.WriteLine($"=== img2img 1024x1024 / 8 steps, strength 0.6, 2 LoRAs @0.85 ===");
    Console.WriteLine($"  init : {(File.Exists(InitImage) ? "OK" : "MISSING")} {InitImage}");
    Console.WriteLine($"  lora1: {(File.Exists(Lora1) ? "OK" : "MISSING")} {Lora1}");
    Console.WriteLine($"  lora2: {(File.Exists(Lora2) ? "OK" : "MISSING")} {Lora2}");

    var initImage = HPPH.SkiaSharp.ImageHelper.LoadImage(InitImage);
    var genParams = ImageGenerationParameter.ImageToImage("photorealistic, realistic skin texture, natural lighting", initImage)
        .WithStrength(0.6f)
        .WithSize(1024, 1024)
        .WithSteps(8)
        .WithCfg(1.0f)
        .WithSampler(Sampler.Euler);
    if (File.Exists(Lora1)) genParams.Loras.Add(new Lora(Lora1) { Multiplier = 0.85f });
    if (File.Exists(Lora2)) genParams.Loras.Add(new Lora(Lora2) { Multiplier = 0.85f });

    var image = model.GenerateImage(genParams);

    var outPath = @"D:\Models\flux2-img2img-smoketest.png";
    File.WriteAllBytes(outPath, image.ToPng());
    Console.WriteLine($">>> GENERATED OK -> {outPath}");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($">>> GENERATE FAILED: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex);
    return 2;
}
finally
{
    model.Dispose();
}
