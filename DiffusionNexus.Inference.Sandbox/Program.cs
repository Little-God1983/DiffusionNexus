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
    Console.WriteLine("=== Generating 1024x1024 / 8 steps (matches canvas default size) ===");
    var image = model.GenerateImage(
        ImageGenerationParameter.TextToImage("a photo of a red apple on a wooden table, studio lighting")
            .WithSize(1024, 1024)
            .WithSteps(8)
            .WithCfg(1.0f)
            .WithSampler(Sampler.Euler));

    var outPath = @"D:\Models\flux2-klein-smoketest.png";
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
