using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class EngineRemovalPlannerTests
{
    private const string Root = @"C:\Engine\ComfyUI";
    private static long Size(string _) => 1000;

    private static ConfigurationCheckResult Result(IEnumerable<(string Path, bool Installed)> nodes, IEnumerable<string[]> models) => new()
    {
        OverallStatus = ConfigurationStatus.Partial,
        CustomNodesStatus = ConfigurationStatus.Partial,
        ModelsStatus = ConfigurationStatus.Partial,
        InstallationType = ComfyUIInstallationType.Manual,
        CustomNodeResults = nodes.Select(n => new CustomNodeCheckResult
        {
            Id = Guid.NewGuid(), Name = Path.GetFileName(n.Path), Url = "u", IsInstalled = n.Installed, ExpectedPath = n.Path
        }).ToList(),
        ModelResults = models.Select(paths => new ModelCheckResult
        {
            Id = Guid.NewGuid(), Name = paths.Length > 0 ? Path.GetFileName(paths[0]) : "missing",
            IsInstalled = paths.Length > 0, SearchedPaths = [],
            FoundAtPath = paths.FirstOrDefault() ?? "", AllFoundPaths = paths
        }).ToList()
    };

    private static EngineFeatureCheck Check(EngineFeature f, string name, ConfigurationCheckResult r) => new(f, name, [r]);

    private const string Gguf = Root + @"\custom_nodes\ComfyUI-GGUF";
    private const string Usdu = Root + @"\custom_nodes\ComfyUI_UltimateSDUpscale";
    private const string EngineVae = Root + @"\models\vae\qwen_image_vae.safetensors";
    private const string EngineUnet = Root + @"\models\diffusion_models\qwen-image-Q4_K_S.gguf";
    private const string UserAe = @"D:\Models\vae\ae.safetensors";
    private const string EngineUpscaler = Root + @"\models\upscale_models\4x-UltraSharp.pth";

    [Fact]
    public void Feature_SharedNodePackAndModel_AreKept_AndNamed()
    {
        var checks = new[]
        {
            Check(EngineFeature.InpaintOutpaint, "Inpaint & Outpaint", Result([(Gguf, true)], [[EngineVae], [EngineUnet]])),
            Check(EngineFeature.OutpaintVision, "Outpaint Vision", Result([(Gguf, true)], [[EngineVae]])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.InpaintOutpaint, Size);

        plan.NodePackFolders.Should().BeEmpty();
        plan.EngineModels.Select(f => f.Path).Should().Equal(EngineUnet);
        plan.Kept.Should().BeEquivalentTo([
            new KeptItem("ComfyUI-GGUF", Gguf, "Outpaint Vision"),
            new KeptItem("qwen_image_vae.safetensors", EngineVae, "Outpaint Vision")]);
    }

    [Fact]
    public void Feature_SharedOnlyWithAFeatureThatIsNotInstalled_IsDeleted()
    {
        var checks = new[]
        {
            Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([(Usdu, true)], [[EngineUpscaler]])),
            Check(EngineFeature.Canvas, "Canvas · Krea 2 Turbo", Result([(Usdu, false)], [Array.Empty<string>()])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.BatchUpscale, Size);

        plan.NodePackFolders.Should().Equal(Usdu);
        plan.EngineModels.Select(f => f.Path).Should().Equal(EngineUpscaler);
        plan.Kept.Should().BeEmpty();
    }

    [Fact]
    public void Feature_EveryCopyCounts_InsideAndOutsideTheEngine()
    {
        var engineAe = Root + @"\models\vae\ae.safetensors";
        var checks = new[] { Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([], [[engineAe, UserAe]])) };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.BatchUpscale, Size);

        plan.EngineModels.Select(f => f.Path).Should().Equal(engineAe);
        plan.OutsideModels.Should().Equal(new PlannedFile(UserAe, 1000));
    }

    [Fact]
    public void Feature_AFolderNamedLikeTheEngineButBesideIt_IsOutside()
    {
        var sibling = @"C:\Engine\ComfyUI-Backup\models\vae\ae.safetensors";
        var checks = new[] { Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([], [[sibling]])) };

        EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.BatchUpscale, Size)
            .OutsideModels.Select(f => f.Path).Should().Equal(sibling);
    }

    [Fact]
    public void Uninstall_ListsOutsideModelsOfPresentFeaturesOnly()
    {
        var checks = new[]
        {
            Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([(Usdu, true)], [[EngineUpscaler, UserAe]])),
            Check(EngineFeature.Canvas, "Canvas · Krea 2 Turbo", Result([], [Array.Empty<string>()])),
        };

        EngineRemovalPlanner.PlanUninstallOutsideModels(Root, checks, Size)
            .Should().Equal(new PlannedFile(UserAe, 1000));
    }

    [Fact]
    public void Uninstall_AFileTwoFeaturesUse_IsListedOnce()
    {
        var checks = new[]
        {
            Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([], [[UserAe]])),
            Check(EngineFeature.BatchUpscaleVision, "Batch Upscale Vision", Result([], [[UserAe]])),
        };

        EngineRemovalPlanner.PlanUninstallOutsideModels(Root, checks, Size).Should().HaveCount(1);
    }

    [Theory]
    [InlineData(@"C:\Engine\ComfyUI", true)]
    [InlineData(@"C:\Engine\ComfyUI\", true)]
    [InlineData(@"D:\", false)]
    [InlineData(@"D:", false)]
    [InlineData(@"D:/", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\server\share\", false)]
    [InlineData(@"\\server\share\Engine", true)]
    [InlineData(@"Engine\ComfyUI", false)]
    [InlineData(@"\Engine\ComfyUI", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsSafeEngineRoot_RefusesBlankRelativeAndDriveRoots(string? root, bool safe) =>
        EngineRemovalPlanner.IsSafeEngineRoot(root).Should().Be(safe);
}
