// THROWAWAY SPIKE — DO NOT BUILD ON TOP OF THIS.
// Goal: Verify that StableDiffusion.NET (SciSharp / DarthAffe) can run our three target architectures
//       on the user's existing ComfyUI .safetensors files with the CUDA12 backend.
//
// Outcome of this spike will determine the shape of the real DiffusionNexus.Inference project.
// If a test fails: the failure mode is what matters, not the success.

using HPPH;
using HPPH.SkiaSharp;
using StableDiffusion.NET;

const string ModelsRoot = @"D:\Matrix\Models";
const string OutputDir = @"D:\Matrix\spike-output";

Directory.CreateDirectory(OutputDir);

// Wire up engine logging up-front so we capture native errors regardless of which test triggers them.
StableDiffusionCpp.InitializeEvents();
StableDiffusionCpp.Log += (_, args) =>
{
    // Filter out the firehose of per-tensor allocation logs; keep warnings/errors and high-level info.
    if (args.Level is LogLevel.Warn or LogLevel.Error)
        Console.WriteLine($"[NATIVE {args.Level}] {args.Text.TrimEnd()}");
};
StableDiffusionCpp.Progress += (_, args) =>
    Console.WriteLine($"  step {args.Step}/{args.Steps}  ({args.Progress * 100:N1}%)  {args.IterationsPerSecond:N2} it/s");

var results = new List<TestResult>
{
    RunTest("SDXL baseline",   () => TestStatus.Skipped),  // skipped: no real SDXL checkpoint identified
    RunTest("Z-Image-Turbo",   TestZImageTurbo),
    RunTest("Qwen-Image-Edit", () => TestStatus.Skipped),  // skipped: needs matching Qwen2.5-VL-7B mmproj
};

PrintSummary(results);
return results.Any(r => r.Status == TestStatus.Failed) ? 1 : 0;

// ─────────────────────────────────────────────────────────────────────────────
// Test 1 — SDXL baseline. Proves engine + CUDA + native binaries work end-to-end
// before we try anything exotic. Picks the largest .safetensors in StableDiffusion/.
// ─────────────────────────────────────────────────────────────────────────────
static TestStatus TestSdxlBaseline()
{
    var sdxlDir = Path.Combine(ModelsRoot, "StableDiffusion");
    var checkpoint = Directory.Exists(sdxlDir)
        ? Directory.EnumerateFiles(sdxlDir, "*.safetensors", SearchOption.TopDirectoryOnly)
            .OrderByDescending(p => new FileInfo(p).Length)
            .FirstOrDefault()
        : null;

    if (checkpoint is null)
    {
        Console.WriteLine($"  no .safetensors found under {sdxlDir} — cannot run baseline.");
        return TestStatus.Skipped;
    }

    var sdxlVae = Path.Combine(ModelsRoot, "VAE", "sdxl_vae.safetensors");
    Console.WriteLine($"  checkpoint : {Path.GetFileName(checkpoint)}");
    Console.WriteLine($"  vae        : {(File.Exists(sdxlVae) ? "sdxl_vae.safetensors" : "(none, using built-in)")}");

    var modelParams = DiffusionModelParameter.Create()
        .WithModelPath(checkpoint)
        .WithMultithreading()
        .WithFlashAttention();

    if (File.Exists(sdxlVae))
        modelParams = modelParams.WithVae(sdxlVae);

    using var sd = new DiffusionModel(modelParams);

    var image = sd.GenerateImage(
        ImageGenerationParameter
            .TextToImage("a photo of a red apple on a wooden table, studio lighting")
            .WithSDXLDefaults());

    SaveImage(image, "spike-1-sdxl.png");
    return TestStatus.Passed;
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 2 — Z-Image-Turbo. The real question of this spike.
// Architecture: Lumina2 family DiT, Qwen-3-4B text encoder, Flux-style ae VAE.
// If this throws "unknown model type" or similar, the engine doesn't support it yet.
// ─────────────────────────────────────────────────────────────────────────────
static TestStatus TestZImageTurbo()
{
    var unet = Path.Combine(ModelsRoot, "DiffusionModels", "z_image_turbo_bf16.safetensors");
    var clip = Path.Combine(ModelsRoot, "TextEncoders", "qwen_3_4b.safetensors");
    var vae = Path.Combine(ModelsRoot, "VAE", "ae.safetensors");

    foreach (var (label, path) in new[] { ("UNET", unet), ("CLIP", clip), ("VAE", vae) })
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"  missing {label}: {path}");
            return TestStatus.Skipped;
        }
        Console.WriteLine($"  {label,-5}: {Path.GetFileName(path)}");
    }

    using var sd = new DiffusionModel(DiffusionModelParameter.Create()
        .WithDiffusionModelPath(unet)
        .WithLLMPath(clip)          // Z-Image-Turbo uses an LLM-style text encoder (Qwen-3-4B), not CLIP
        .WithVae(vae)
        .WithMultithreading()
        .WithFlashAttention());

    var image = sd.GenerateImage(
        ImageGenerationParameter
            .TextToImage("a photo of a red apple on a wooden table, studio lighting")
            .WithSize(1024, 1024)
            .WithSteps(9)
            .WithCfg(1.0f)
            .WithSampler(Sampler.Euler));

    SaveImage(image, "spike-2-zimage-turbo.png");
    return TestStatus.Passed;
}

// ─────────────────────────────────────────────────────────────────────────────
// Test 3 — Qwen-Image-Edit. Only attempted if mmproj vision adapter is found.
// stable-diffusion.cpp wants 4 separate files; ComfyUI bundles vision encoder
// and mmproj into one. If mmproj is missing, skip with a clear "what to do" message.
// ─────────────────────────────────────────────────────────────────────────────
static TestStatus TestQwenImageEdit()
{
    var qwenImage = FindFirst(ModelsRoot, "qwen_image*.safetensors", excludeKeywords: ["vae", "lora", "lightning"]);
    var qwenVl = FindFirst(Path.Combine(ModelsRoot, "TextEncoders"), "qwen_2.5_vl*.safetensors");
    var qwenVae = Path.Combine(ModelsRoot, "VAE", "qwen_image_vae.safetensors");
    var mmproj = FindFirst(ModelsRoot, "*qwen*mmproj*.gguf")
              ?? FindFirst(ModelsRoot, "*qwen*mmproj*.safetensors")
              ?? FindFirst(ModelsRoot, "*mmproj*qwen*.gguf");

    Console.WriteLine($"  diffusion model : {qwenImage ?? "(NOT FOUND)"}");
    Console.WriteLine($"  qwen2-vl text   : {qwenVl ?? "(NOT FOUND)"}");
    Console.WriteLine($"  qwen2-vl mmproj : {mmproj ?? "(NOT FOUND — required for vision input)"}");
    Console.WriteLine($"  vae             : {(File.Exists(qwenVae) ? qwenVae : "(NOT FOUND)")}");

    if (qwenImage is null || qwenVl is null || mmproj is null || !File.Exists(qwenVae))
    {
        Console.WriteLine("  one or more required Qwen-Image-Edit files missing.");
        Console.WriteLine("  download mmproj from: https://huggingface.co/Qwen/Qwen2.5-VL-7B-Instruct (mmproj-*.gguf)");
        return TestStatus.Skipped;
    }

    using var sd = new DiffusionModel(DiffusionModelParameter.Create()
        .WithDiffusionModelPath(qwenImage)
        .WithLLMPath(qwenVl)
        .WithLLMVisionPath(mmproj)
        .WithVae(qwenVae)
        .WithMultithreading()
        .WithFlashAttention()
        .WithOffloadedParamsToCPU()
        .WithImmediatelyFreedParams());

    // Edit path requires a reference image; reuse the SDXL output if present so we don't add a fixture dep.
    var refPath = Path.Combine(OutputDir, "spike-1-sdxl.png");
    if (!File.Exists(refPath))
    {
        Console.WriteLine("  no reference image (spike-1-sdxl.png) available — running text-to-image instead.");
        var t2i = sd.GenerateImage(ImageGenerationParameter.TextToImage("a red apple on a wooden table")
            .WithSize(1024, 1024).WithCfg(2.5f).WithSampler(Sampler.Euler));
        SaveImage(t2i, "spike-3-qwen-image.png");
        return TestStatus.Passed;
    }

    var refImg = (IImage<ColorRGB>)HPPH.SkiaSharp.ImageHelper.LoadImage(refPath);
    var edited = sd.GenerateImage(ImageGenerationParameter
        .TextToImage("change the apple to a green pear, keep everything else identical")
        .WithSize(1024, 1024)
        .WithCfg(2.5f)
        .WithSampler(Sampler.Euler)
        .WithRefImages(refImg));

    SaveImage(edited, "spike-3-qwen-image-edit.png");
    return TestStatus.Passed;
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────
static TestResult RunTest(string name, Func<TestStatus> test)
{
    Console.WriteLine();
    Console.WriteLine(new string('═', 72));
    Console.WriteLine($"▶  {name}");
    Console.WriteLine(new string('═', 72));

    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var status = test();
        sw.Stop();
        return new TestResult(name, status, sw.Elapsed, null);
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"  ✘ EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace is not null)
            Console.WriteLine(ex.StackTrace);
        return new TestResult(name, TestStatus.Failed, sw.Elapsed, ex);
    }
}

static void SaveImage(IImage<ColorRGB>? image, string fileName)
{
    if (image is null)
    {
        Console.WriteLine($"  ✘ generation returned null image for {fileName}");
        return;
    }
    var path = Path.Combine(OutputDir, fileName);
    File.WriteAllBytes(path, image.ToPng());
    Console.WriteLine($"  ✔ saved {path} ({new FileInfo(path).Length / 1024} KB)");
}

static string? FindFirst(string root, string pattern, string[]? excludeKeywords = null)
{
    if (!Directory.Exists(root)) return null;
    return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
        .FirstOrDefault(p => excludeKeywords is null
            || !excludeKeywords.Any(k => Path.GetFileName(p).Contains(k, StringComparison.OrdinalIgnoreCase)));
}

static void PrintSummary(IReadOnlyList<TestResult> results)
{
    Console.WriteLine();
    Console.WriteLine(new string('═', 72));
    Console.WriteLine("  SUMMARY");
    Console.WriteLine(new string('═', 72));
    foreach (var r in results)
    {
        var icon = r.Status switch
        {
            TestStatus.Passed => "OK ",
            TestStatus.Skipped => "-- ",
            TestStatus.Failed => "FAIL",
            _ => "?? "
        };
        Console.WriteLine($"  [{icon}] {r.Name,-20}  {r.Status,-8}  {r.Duration.TotalSeconds,7:N1}s");
        if (r.Exception is not null)
            Console.WriteLine($"        └─ {r.Exception.GetType().Name}: {r.Exception.Message}");
    }
    Console.WriteLine(new string('═', 72));
}

internal enum TestStatus { Passed, Failed, Skipped }
internal sealed record TestResult(string Name, TestStatus Status, TimeSpan Duration, Exception? Exception);
