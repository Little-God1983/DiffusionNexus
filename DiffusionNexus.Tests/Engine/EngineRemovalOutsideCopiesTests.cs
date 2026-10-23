using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

/// <summary>
/// The removal plan over the real checker: what reads as installed agrees with the Features dialog, and a
/// copy in the user's own folders is offered for deletion only when it sits in the model's category folder
/// under a name the installer leaves on disk.
/// </summary>
public sealed class EngineRemovalOutsideCopiesTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("dn-removal-outside-");
    private readonly string _repo;
    private readonly string _library;

    public EngineRemovalOutsideCopiesTests()
    {
        _repo = Path.Combine(_temp.FullName, "Engine");
        _library = Path.Combine(_temp.FullName, "Library");
        Directory.CreateDirectory(Path.Combine(_repo, "models"));
        Directory.CreateDirectory(_library);
        File.WriteAllText(Path.Combine(_repo, "main.py"), "");
        File.WriteAllText(Path.Combine(_repo, "extra_model_paths.yaml"),
            $"dn_models_1:\n    base_path: {_library.Replace('\\', '/')}\n    controlnet: controlnet/\n");
    }

    public void Dispose() { try { _temp.Delete(recursive: true); } catch { /* best effort */ } }

    private static ModelDownload Model(string name, string destination, string url) => new()
    {
        Name = name, Destination = destination, DownloadLinks = [new ModelDownloadLink { Url = url }]
    };

    private static InstallationConfiguration Config(params ModelDownload[] models)
    {
        var config = new InstallationConfiguration { Name = "t" };
        foreach (var model in models) config.ModelDownloads.Add(model);
        return config;
    }

    private string Put(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private async Task<EngineFeatureCheck> CheckAsync(EngineFeature feature, string name, InstallationConfiguration config) =>
        new(feature, name, [await new ConfigurationCheckerService().CheckConfigurationAsync(
            config, _repo, new ConfigurationCheckOptions { CollectAllMatches = true })]);

    private async Task<EngineRemovalPlan> PlanOneAsync(ModelDownload model)
    {
        var checks = new[] { await CheckAsync(EngineFeature.BatchUpscale, "Batch Upscale", Config(model)) };
        return EngineRemovalPlanner.PlanFeatureRemoval(_repo, checks, EngineFeature.BatchUpscale, _ => 1);
    }

    // (i) Canvas's diffusion model sits in the user's library under its URL name (the user's own ComfyUI
    // downloaded it). The dialog reads Canvas as Installed, so the removal plan does too: Canvas keeps the
    // VAE it shares with the removed feature.
    [Fact]
    public async Task AFeatureWhoseLibraryCopyHasOnlyItsUrlName_IsFullyInstalled_AndKeepsTheSharedFile()
    {
        var vae = Model("qwen_image_vae", "models/vae", "https://x/qwen_image_vae.safetensors");
        var vaePath = Put(_repo, @"models\vae\qwen_image_vae.safetensors");
        var ownPath = Put(_repo, @"models\checkpoints\target_own.safetensors");
        Put(_library, @"diffusion_models\qwen-image-Q4_K_S.gguf");
        var checks = new[]
        {
            await CheckAsync(EngineFeature.OutpaintVision, "Outpaint Vision",
                Config(vae, Model("target_own", "models/checkpoints", "https://x/target_own.safetensors"))),
            await CheckAsync(EngineFeature.Canvas, "Canvas · Krea 2 Turbo",
                Config(vae, Model("Qwen Image", "models/diffusion_models", "https://huggingface.co/x/resolve/main/qwen-image-Q4_K_S.gguf"))),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(_repo, checks, EngineFeature.OutpaintVision, _ => 1);

        plan.Kept.Select(k => k.Path).Should().Equal(vaePath);
        plan.Kept.Single().NeededBy.Should().Be("Canvas · Krea 2 Turbo");
        plan.EngineModels.Select(f => f.Path).Should().Equal(ownPath);
    }

    // (ii) + (iv): a vae model's same-named file in the library's loras folder is not offered; the one in
    // its vae folder is.
    [Fact]
    public async Task OnlyTheCopyInTheModelsCategoryFolder_IsOffered()
    {
        var model = Model("ae", "models/vae", "https://x/ae.safetensors");
        Put(_library, @"loras\ae.safetensors");
        var inVae = Put(_library, @"vae\ae.safetensors");

        var plan = await PlanOneAsync(model);

        plan.OutsideModels.Select(f => f.Path).Should().Equal(inVae);
    }

    // (iii): a generic HuggingFace name the installer renames is never offered, not in another ControlNet's
    // subfolder and not in the category folder either; the configured name in the category folder is.
    [Fact]
    public async Task ARenamedUrlName_IsNotOffered_TheConfiguredNameIs()
    {
        var model = Model("InstantX-Union", "models/controlnet", "https://huggingface.co/x/resolve/main/diffusion_pytorch_model.safetensors");
        Put(_library, @"controlnet\OtherNet\diffusion_pytorch_model.safetensors");
        Put(_library, @"controlnet\diffusion_pytorch_model.safetensors");
        var configured = Put(_library, @"controlnet\InstantX-Union.safetensors");

        var plan = await PlanOneAsync(model);

        plan.OutsideModels.Select(f => f.Path).Should().Equal(configured);
    }

    // (v) ComfyUI's legacy folder names count as the category.
    [Theory]
    [InlineData("models/diffusion_models", "unet")]
    [InlineData("models/unet", "diffusion_models")]
    [InlineData("models/text_encoders", "clip")]
    [InlineData("models/clip", "text_encoders")]
    public async Task ALegacyAliasFolder_CountsAsTheCategory(string destination, string folder)
    {
        var model = Model("m", destination, "https://x/m.safetensors");
        var copy = Put(_library, Path.Combine(folder, "m.safetensors"));

        var plan = await PlanOneAsync(model);

        plan.OutsideModels.Select(f => f.Path).Should().Equal(copy);
    }

    // Uninstall's outside list follows the same rule; a filtered copy still makes the model installed.
    [Fact]
    public async Task UninstallOutsideList_OffersOnlyCopiesInTheCategoryFolder()
    {
        var model = Model("ae", "models/vae", "https://x/ae.safetensors");
        Put(_library, @"loras\ae.safetensors");
        var check = await CheckAsync(EngineFeature.BatchUpscale, "Batch Upscale", Config(model));
        check.Results.Single().ModelResults.Single().IsInstalled.Should().BeTrue();

        EngineRemovalPlanner.PlanUninstallOutsideModels(_repo, [check], _ => 1).Should().BeEmpty();

        var inVae = Put(_library, @"vae\ae.safetensors");
        check = await CheckAsync(EngineFeature.BatchUpscale, "Batch Upscale", Config(model));
        EngineRemovalPlanner.PlanUninstallOutsideModels(_repo, [check], _ => 1).Select(f => f.Path).Should().Equal(inVae);
    }
}
