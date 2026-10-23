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
            // The folder the last copy sits in (the user's copy, where a fixture has one), so a copy outside
            // the Engine is in its category folder.
            Category = paths.Length > 0 ? Path.GetFileName(Path.GetDirectoryName(paths[^1])) : null,
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

    // E: Inpaint & Outpaint's files are a subset of Outpaint Vision's, but Canvas keeps the shared VAE.
    // Removing Outpaint Vision leaves Inpaint & Outpaint with that VAE: partly installed, not removed too.
    [Fact]
    public void SubsetFeature_WithAFileAThirdFeatureKeeps_IsLeftPartial_NotRemovedWithIt()
    {
        var krea = Root + @"\models\diffusion_models\krea2_turbo-Q4_K_S.gguf";
        var checks = new[]
        {
            Check(EngineFeature.InpaintOutpaint, "Inpaint & Outpaint", Result([(Gguf, true)], [[EngineVae], [EngineUnet]])),
            Check(EngineFeature.OutpaintVision, "Outpaint Vision", Result([(Gguf, true), (VlNode, true)], [[EngineVae], [EngineUnet], [EngineVl]])),
            Check(EngineFeature.Canvas, "Canvas · Krea 2 Turbo", Result([], [[EngineVae], [krea]])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.OutpaintVision, Size);

        plan.Kept.Should().Equal(new KeptItem("qwen_image_vae.safetensors", EngineVae, "Canvas · Krea 2 Turbo"));
        plan.EngineModels.Select(f => f.Path).Should().BeEquivalentTo([EngineUnet, EngineVl]);
        plan.RemovedWithIt.Should().BeEmpty();
        plan.LeftPartial.Should().Equal("Inpaint & Outpaint");
    }

    // H: the shipping catalog's three overlapping features. Outpaint Vision and Batch Upscale Vision share the
    // Qwen3-VL node and model, Outpaint Vision and Canvas the VAE, Batch Upscale Vision and Canvas
    // 4x-UltraSharp and Ultimate SD Upscale; each has a file of its own. With a Partial feature still
    // protecting, removing each once left {VL, vae}, {VL, 4x, USDU}, {vae, 4x, USDU}, each keeping the others'
    // files, and every further Remove said nothing could be deleted. Only a fully installed feature protects.
    private static readonly string ZImage = Root + @"\models\diffusion_models\z_image_turbo_bf16.safetensors";
    private static readonly string Krea = Root + @"\models\diffusion_models\krea2_turbo-Q4_K_S.gguf";

    private static readonly (EngineFeature Feature, string Name, string[] Nodes, string[] Models)[] Shipping =
    [
        (EngineFeature.OutpaintVision, "Outpaint Vision", [VlNode], [EngineVl, EngineVae, EngineUnet]),
        (EngineFeature.BatchUpscaleVision, "Batch Upscale Vision", [VlNode, Usdu], [EngineVl, EngineUpscaler, ZImage]),
        (EngineFeature.Canvas, "Canvas · Krea 2 Turbo", [Usdu], [EngineVae, EngineUpscaler, Krea]),
    ];

    /// <summary>Each feature as the checker sees it with only <paramref name="onDisk"/> on disk.</summary>
    private static EngineFeatureCheck[] ChecksFor(HashSet<string> onDisk) => Shipping.Select(f => Check(f.Feature, f.Name,
        Result(f.Nodes.Select(n => (n, onDisk.Contains(n))), f.Models.Select(m => onDisk.Contains(m) ? new[] { m } : [])))).ToArray();

    [Theory]
    [InlineData(EngineFeature.OutpaintVision, EngineFeature.BatchUpscaleVision, EngineFeature.Canvas)]
    [InlineData(EngineFeature.Canvas, EngineFeature.BatchUpscaleVision, EngineFeature.OutpaintVision)]
    [InlineData(EngineFeature.BatchUpscaleVision, EngineFeature.OutpaintVision, EngineFeature.Canvas)]
    public void ThreeOverlappingFeatures_RemovedOnceEach_LeaveNothingBehind(EngineFeature first, EngineFeature second, EngineFeature third)
    {
        var onDisk = new HashSet<string>(Shipping.SelectMany(f => f.Nodes.Concat(f.Models)), StringComparer.OrdinalIgnoreCase);

        foreach (var target in new[] { first, second, third })
        {
            var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, ChecksFor(onDisk), target, Size);
            plan.HasAnythingToDelete.Should().BeTrue($"removing {target} must delete something");
            onDisk.ExceptWith(plan.NodePackFolders.Concat(plan.EngineModels.Select(f => f.Path)));
        }

        onDisk.Should().BeEmpty("every file of the three features was removable");
    }

    // H: a fully installed feature protects what it shares; a Partial one protects nothing, loses the
    // shared file and is named as left Partial (it keeps a file of its own).
    [Fact]
    public void APartialFeature_ProtectsNothing_AndIsNamedAsLeftPartial()
    {
        var checks = new[]
        {
            Check(EngineFeature.OutpaintVision, "Outpaint Vision", Result([(VlNode, true)], [[EngineVl], [EngineVae], [EngineUnet]])),
            // Batch Upscale Vision lacks its upscaler: Partial.
            Check(EngineFeature.BatchUpscaleVision, "Batch Upscale Vision", Result([(VlNode, true), (Usdu, true)], [[EngineVl], [], [ZImage]])),
            // Canvas is fully installed and shares the VAE.
            Check(EngineFeature.Canvas, "Canvas · Krea 2 Turbo", Result([], [[EngineVae], [Krea]])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.OutpaintVision, Size);

        plan.NodePackFolders.Should().Equal(VlNode);
        plan.EngineModels.Select(f => f.Path).Should().BeEquivalentTo([EngineVl, EngineUnet]);
        plan.Kept.Should().Equal(new KeptItem("qwen_image_vae.safetensors", EngineVae, "Canvas · Krea 2 Turbo"));
        plan.LeftPartial.Should().Equal("Batch Upscale Vision");
        plan.RemovedWithIt.Should().BeEmpty();
    }

    // H with the known gap: a model whose download URL carries no file name (Krea 2 Turbo's Identity Edit
    // LoRA) is never found by name. It does not make an otherwise complete feature Partial, so Canvas
    // keeps protecting the VAE it shares.
    [Fact]
    public void AModelTheCheckerCannotFindByName_DoesNotStopAFeatureFromProtecting()
    {
        var canvas = Result([], [[EngineVae], [Krea]]);
        canvas = canvas with
        {
            ModelResults = [.. canvas.ModelResults, new ModelCheckResult
            {
                Id = Guid.NewGuid(), Name = "Krea 2 Identity Edit", IsInstalled = false, IsFindableByName = false,
                SearchedPaths = [Root + @"\models\loras\3139172"]
            }]
        };
        var checks = new[]
        {
            Check(EngineFeature.InpaintOutpaint, "Inpaint & Outpaint", Result([(Gguf, true)], [[EngineVae], [EngineUnet]])),
            Check(EngineFeature.Canvas, "Canvas · Krea 2 Turbo", canvas),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.InpaintOutpaint, Size);

        plan.Kept.Should().Equal(new KeptItem("qwen_image_vae.safetensors", EngineVae, "Canvas · Krea 2 Turbo"));
        plan.EngineModels.Select(f => f.Path).Should().Equal(EngineUnet);
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

    // R3-1: in the nested layout the Engine's files are its ComfyUI clone (and venv); the user's own library
    // next to them in the folder they picked is theirs: listed and optional, never an Engine deletion.
    [Fact]
    public void Feature_NestedLayout_ACopyBesideTheEnginesParts_IsTheUsers()
    {
        var root = Directory.CreateTempSubdirectory("dn-planner-nested-").FullName;
        try
        {
            Write(root, @"ComfyUI\main.py", 0);
            var engineCopy = Write(root, @"ComfyUI\models\vae\ae.safetensors", 1);
            var userCopy = Write(root, @"MyModels\vae\ae.safetensors", 1);
            var checks = new[] { Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([], [[engineCopy, userCopy]])) };

            var plan = EngineRemovalPlanner.PlanFeatureRemoval(root, checks, EngineFeature.BatchUpscale, Size);

            plan.EngineModels.Select(f => f.Path).Should().Equal(engineCopy);
            plan.OutsideModels.Select(f => f.Path).Should().Equal(userCopy);
            EngineRemovalPlanner.PlanUninstallOutsideModels(root, checks, Size).Select(f => f.Path).Should().Equal(userCopy);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static EngineFeatureCheck OneModel(string name, string category, params string[] copies) =>
        Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([], [copies]) with
        {
            ModelResults = [new ModelCheckResult
            {
                Id = Guid.NewGuid(), Name = name, IsInstalled = true, SearchedPaths = [], Category = category,
                FoundAtPath = copies[0], AllFoundPaths = copies
            }]
        });

    // R3-2: the checker matches by name in every folder under models\, so an earlier Core download in
    // models\controlnet with the same name is not this vae model's file: not deleted, not counted.
    [Fact]
    public void Feature_AnEngineCopyInAnotherCategoryFolder_IsNotTheFeatures()
    {
        var other = Root + @"\models\controlnet\ae.safetensors";
        var mine = Root + @"\models\vae\ae.safetensors";
        var checks = new[] { OneModel("ae", "vae", other, mine) };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.BatchUpscale, Size);

        plan.EngineModels.Select(f => f.Path).Should().Equal(mine);
        plan.EngineBytes.Should().Be(1000);
    }

    // R3-2: such a copy is one no feature declares, so Uninstall counts it with the other model files.
    [Fact]
    public void Uninstall_AnEngineCopyInAnotherCategoryFolder_CountsAsUnclaimed()
    {
        var root = Directory.CreateTempSubdirectory("dn-unclaimed-category-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "main.py"), "");
            var other = Write(root, @"models\controlnet\ae.safetensors", 30);
            var mine = Write(root, @"models\vae\ae.safetensors", 10);

            EngineRemovalPlanner.PlanUninstallUnclaimedModels(root, [OneModel("ae", "vae", other, mine)])
                .Should().Be(new ModelFileTotal(1, 30));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // R3-3: models\loras inside the Engine is a junction to the user's library. A copy found through it is
    // the user's file behind a link: it is not planned as an Engine deletion and its size is not counted.
    [Fact]
    public void Feature_AnEngineCopyReachedThroughALink_IsNotPlanned()
    {
        var root = Directory.CreateTempSubdirectory("dn-planner-linked-").FullName;
        var library = Directory.CreateTempSubdirectory("dn-planner-linked-lib-").FullName;
        var link = Path.Combine(root, "models", "loras");
        try
        {
            File.WriteAllText(Path.Combine(root, "main.py"), "");
            Directory.CreateDirectory(Path.Combine(root, "models"));
            Write(library, "style.safetensors", 50);
            Junction(link, library);
            var throughLink = Path.Combine(link, "style.safetensors");
            var engineVae = Write(root, @"models\vae\ae.safetensors", 1);
            var feature = OneModel("style", "loras", throughLink);
            var checks = new[] { feature with { Results = [.. feature.Results, Result([], [[engineVae]])] } };

            var plan = EngineRemovalPlanner.PlanFeatureRemoval(root, checks, EngineFeature.BatchUpscale, Size);

            plan.EngineModels.Select(f => f.Path).Should().Equal(engineVae);
            plan.OutsideModels.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // the link only, before the trees
            Directory.Delete(root, recursive: true);
            Directory.Delete(library, recursive: true);
        }
    }

    // R3-5: Batch Upscale uses only Batch Upscale Vision's files, but its upscaler is a copy in the user's
    // folder. It goes too only if the user deletes all models; with "only what's in the Engine" it keeps
    // that copy and shows as Partial.
    [Fact]
    public void ASubsetFeatureRelyingOnAnOfferedOutsideCopy_IsRemovedOnlyWithEverything()
    {
        const string userUpscaler = @"D:\Models\upscale_models\4x-UltraSharp.pth";
        var checks = new[]
        {
            Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([(Usdu, true)], [[userUpscaler]])),
            Check(EngineFeature.BatchUpscaleVision, "Batch Upscale Vision", Result([(Usdu, true), (VlNode, true)], [[userUpscaler], [EngineVl]])),
        };

        var plan = EngineRemovalPlanner.PlanFeatureRemoval(Root, checks, EngineFeature.BatchUpscaleVision, Size);

        plan.OutsideModels.Select(f => f.Path).Should().Equal(userUpscaler);
        plan.RemovedWithIt.Should().BeEmpty();
        plan.RemovedIfEverything.Should().Equal(new ConditionalRemoval("Batch Upscale", PartialWithEngineOnly: true));
    }

    // R3-2: ComfyUI's legacy folder names count inside the Engine too.
    [Theory]
    [InlineData("diffusion_models", "unet")]
    [InlineData("text_encoders", "clip")]
    public void Feature_AnEngineCopyInALegacyAliasFolder_IsTheFeatures(string category, string folder)
    {
        var copy = Root + $@"\models\{folder}\m.safetensors";

        EngineRemovalPlanner.PlanFeatureRemoval(Root, [OneModel("m", category, copy)], EngineFeature.BatchUpscale, Size)
            .EngineModels.Select(f => f.Path).Should().Equal(copy);
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
            Check(EngineFeature.BatchUpscale, "Batch Upscale", Result([(Usdu, true)], [[Root + @"\models\vae\ae.safetensors", UserAe]])),
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

    // Round 2: when the Engine's models folder (or a folder between it and the Engine root) is a link to the
    // user's library, only the link goes with the Engine: the files behind it are not counted.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uninstall_ALinkedModelsFolder_IsNotCounted(bool portableLayout)
    {
        var root = Directory.CreateTempSubdirectory("dn-unclaimed-linked-").FullName;
        var library = Directory.CreateTempSubdirectory("dn-unclaimed-linked-lib-").FullName;
        var repo = portableLayout ? Path.Combine(root, "ComfyUI") : root;
        // Portable: the ComfyUI folder itself is the link; otherwise the models folder is.
        var link = portableLayout ? repo : Path.Combine(root, "models");
        try
        {
            Write(library, portableLayout ? "main.py" : "unused.txt", 0);
            Write(library, portableLayout ? @"models\checkpoints\mine.safetensors" : @"checkpoints\mine.safetensors", 100);
            if (!portableLayout) File.WriteAllText(Path.Combine(root, "main.py"), "");
            Junction(link, library);

            EngineRemovalPlanner.PlanUninstallUnclaimedModels(root, []).Should().BeNull();
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
