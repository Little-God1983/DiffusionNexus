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
    private const string VlNode = Root + @"\custom_nodes\ComfyUI_Simple_Qwen3-VL-gguf";
    private const string EngineVl = Root + @"\models\text_encoders\Qwen3-VL-8B-Q4_K_M.gguf";

    [Fact]
    public void Feature_SharedNodePackAndModel_AreKept_AndNamed()
    {
        // Outpaint Vision has a node pack of its own, so it is installed on its own and needs the shared files.
        var checks = new[]
        {
            Check(EngineFeature.InpaintOutpaint, "Inpaint & Outpaint", Result([(Gguf, true)], [[EngineVae], [EngineUnet]])),
            Check(EngineFeature.OutpaintVision, "Outpaint Vision", Result([(Gguf, true), (VlNode, true)], [[EngineVae]])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.InpaintOutpaint, Size);

        plan.NodePackFolders.Should().BeEmpty();
        plan.EngineModels.Select(f => f.Path).Should().Equal(EngineUnet);
        plan.Kept.Should().BeEquivalentTo([
            new KeptItem("ComfyUI-GGUF", Gguf, "Outpaint Vision"),
            new KeptItem("qwen_image_vae.safetensors", EngineVae, "Outpaint Vision")]);
    }

    // C1: Inpaint & Outpaint's files are a subset of Outpaint Vision's. Removing the small one first keeps
    // everything (Vision has files of its own and needs the shared ones); removing Vision next must then
    // delete the shared files, because Inpaint & Outpaint has nothing of its own to need them for.
    [Fact]
    public void SubsetPair_SmallFirst_KeepsTheSharedFiles_ThenRemovingTheBigOneDeletesThem()
    {
        var checks = new[]
        {
            Check(EngineFeature.InpaintOutpaint, "Inpaint & Outpaint", Result([(Gguf, true)], [[EngineVae], [EngineUnet]])),
            Check(EngineFeature.OutpaintVision, "Outpaint Vision", Result([(Gguf, true), (VlNode, true)], [[EngineVae], [EngineUnet], [EngineVl]])),
        };

        var first = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.InpaintOutpaint, Size);
        first.HasAnythingToDelete.Should().BeFalse("every file of Inpaint & Outpaint is one Outpaint Vision needs");
        first.Kept.Should().HaveCount(3).And.OnlyContain(k => k.NeededBy == "Outpaint Vision");

        // Nothing was deleted, so the disk (and the checks) are the same for the second removal.
        var second = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.OutpaintVision, Size);

        second.NodePackFolders.Should().BeEquivalentTo([Gguf, VlNode]);
        second.EngineModels.Select(f => f.Path).Should().BeEquivalentTo([EngineVae, EngineUnet, EngineVl]);
        second.Kept.Should().BeEmpty();
        second.RemovedWithIt.Should().Equal("Inpaint & Outpaint");
    }

    // C1, the other order: removing the big one first deletes the shared files too (the small one has
    // nothing of its own), and the plan names the feature that goes with it; then the small one is gone.
    [Fact]
    public void SubsetPair_BigFirst_DeletesTheSharedFiles_AndNamesTheFeatureThatGoesWithIt()
    {
        var checks = new[]
        {
            Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([(Usdu, true)], [[EngineUpscaler]])),
            Check(EngineFeature.BatchUpscaleVision, "Batch Upscale Vision", Result([(Usdu, true), (VlNode, true)], [[EngineUpscaler], [EngineVl]])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.BatchUpscaleVision, Size);

        plan.NodePackFolders.Should().BeEquivalentTo([Usdu, VlNode]);
        plan.EngineModels.Select(f => f.Path).Should().BeEquivalentTo([EngineUpscaler, EngineVl]);
        plan.Kept.Should().BeEmpty();
        plan.RemovedWithIt.Should().Equal("Batch Upscale");
    }

    // C1, the dead end the earlier rule left behind (big one removed first, shared files kept): both
    // features present on the same shared files only. Each named the other as the owner, so neither could
    // ever be removed. Now the second removal, of either one, deletes them.
    [Theory]
    [InlineData(EngineFeature.InpaintOutpaint)]
    [InlineData(EngineFeature.OutpaintVision)]
    public void TwoFeaturesOnTheSameSharedFilesOnly_EitherRemovalDeletesThem(EngineFeature target)
    {
        var checks = new[]
        {
            Check(EngineFeature.InpaintOutpaint, "Inpaint & Outpaint", Result([(Gguf, true)], [[EngineVae], [EngineUnet]])),
            Check(EngineFeature.OutpaintVision, "Outpaint Vision", Result([(Gguf, true), (VlNode, false)], [[EngineVae], [EngineUnet], []])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, target, Size);

        plan.NodePackFolders.Should().Equal(Gguf);
        plan.EngineModels.Select(f => f.Path).Should().BeEquivalentTo([EngineVae, EngineUnet]);
        plan.Kept.Should().BeEmpty();
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

    // C2: model files in the Engine's models folder that no feature declares (a Core download that landed
    // there) are counted, so the uninstall question can say they go with the Engine. ComfyUI's own
    // placeholders and configs are not model files; a junction into the user's library is not entered.
    [Fact]
    public void Uninstall_CountsModelFilesNoFeatureDeclares()
    {
        var root = Directory.CreateTempSubdirectory("dn-unclaimed-").FullName;
        var library = Directory.CreateTempSubdirectory("dn-unclaimed-lib-").FullName;
        var link = Path.Combine(root, "models", "loras");
        try
        {
            File.WriteAllText(Path.Combine(root, "main.py"), "");
            var featureModel = Write(root, @"models\vae\ae.safetensors", 10);
            Write(root, @"models\checkpoints\core-download.safetensors", 100);
            Write(root, @"models\diffusion_models\sub\other.gguf", 50);
            Write(root, @"models\checkpoints\put_checkpoints_here", 0);
            Write(root, @"models\configs\v1-inference.yaml", 5);
            Write(library, @"my-lora.safetensors", 7);
            Junction(link, library);

            var checks = new[] { Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([], [[featureModel.ToUpperInvariant()]])) };

            EngineRemovalPlanner.PlanUninstallUnclaimedModels(root, checks).Should().Be(new ModelFileTotal(2, 150));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // the link only, before the trees
            Directory.Delete(root, recursive: true);
            Directory.Delete(library, recursive: true);
        }
    }

    [Fact]
    public void Uninstall_NoModelsFolder_CountsNothing()
    {
        var root = Directory.CreateTempSubdirectory("dn-unclaimed-").FullName;
        try
        {
            EngineRemovalPlanner.PlanUninstallUnclaimedModels(root, []).Should().Be(new ModelFileTotal(0, 0));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Write(string root, string relative, int bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Directory.Exists(link).Should().BeTrue("the junction must exist for this test");
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
