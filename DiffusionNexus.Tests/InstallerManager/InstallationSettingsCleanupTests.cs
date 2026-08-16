using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Covers <see cref="InstallationSettingsCleanup"/> — resolving which settings rows
/// (Generation Galleries, LoRA sources, Base Model Folders) belong to an installation
/// that is being removed from the Installer Manager. Galleries and Base Model Folders
/// are matched by their InstallerPackageId FK; LoRA sources have no FK and are matched
/// by living underneath the installation path; Base Model Folders additionally match
/// by path so manually re-added rows under the install are offered too.
/// </summary>
public sealed class InstallationSettingsCleanupTests
{
    private static InstallerPackage Package(int id = 5, string path = @"C:\AI\ComfyUI") => new()
    {
        Id = id,
        Name = "ComfyUI",
        InstallationPath = path,
        ExecutablePath = "main.py",
        Type = InstallerType.ComfyUI,
    };

    [Fact]
    public void Resolve_MatchesGalleries_ByPackageLinkOnly()
    {
        var linked = new ImageGallery { Id = 1, FolderPath = @"D:\Output", InstallerPackageId = 5 };
        var otherPackage = new ImageGallery { Id = 2, FolderPath = @"C:\AI\ComfyUI\output", InstallerPackageId = 9 };
        var unlinked = new ImageGallery { Id = 3, FolderPath = @"C:\AI\ComfyUI\output2" };
        var settings = new AppSettings { Id = 1, ImageGalleries = [linked, otherPackage, unlinked] };

        var plan = InstallationSettingsCleanup.Resolve(settings, Package());

        plan.Galleries.Should().ContainSingle().Which.Should().BeSameAs(linked,
            "the FK is the ownership marker — a gallery merely located under the install path is not offered");
    }

    [Fact]
    public void Resolve_MatchesLoraSources_UnderTheInstallationPath()
    {
        var inside = new LoraSource { Id = 1, FolderPath = @"C:\AI\ComfyUI\models\loras" };
        var caseInsensitive = new LoraSource { Id = 2, FolderPath = @"c:\ai\comfyui\MODELS\Loras2\" };
        var outside = new LoraSource { Id = 3, FolderPath = @"D:\Loras" };
        var prefixNotFolder = new LoraSource { Id = 4, FolderPath = @"C:\AI\ComfyUI-Backup\loras" };
        var settings = new AppSettings { Id = 1, LoraSources = [inside, caseInsensitive, outside, prefixNotFolder] };

        var plan = InstallationSettingsCleanup.Resolve(settings, Package());

        plan.LoraSources.Should().BeEquivalentTo(new[] { inside, caseInsensitive },
            o => o.WithoutStrictOrdering(),
            "\"C:\\AI\\ComfyUI-Backup\" is a sibling folder, not a subfolder of the installation");
    }

    [Fact]
    public void Resolve_MatchesBaseModelFolders_ByOwnLinkOrUnclaimedPath()
    {
        var byLink = new BaseModelFolder { Id = 1, FolderPath = @"E:\ExtraModels", InstallerPackageId = 5 };
        var byPath = new BaseModelFolder { Id = 2, FolderPath = @"C:\AI\ComfyUI\models" };
        var unrelated = new BaseModelFolder { Id = 3, FolderPath = @"D:\Models", InstallerPackageId = 9 };
        var otherOwnersRow = new BaseModelFolder { Id = 4, FolderPath = @"C:\AI\ComfyUI\models\shared", InstallerPackageId = 9 };
        var settings = new AppSettings { Id = 1, BaseModelFolders = [byLink, byPath, unrelated, otherOwnersRow] };

        var plan = InstallationSettingsCleanup.Resolve(settings, Package());

        plan.BaseModelFolders.Should().BeEquivalentTo(new[] { byLink, byPath },
            o => o.WithoutStrictOrdering(),
            "a row registered for ANOTHER still-existing installation must never be swept, even under this install's path");
    }

    [Fact]
    public void Resolve_KeepsBaseModelFolders_AnotherInstallationStillDiscovers()
    {
        // Install B's extra_model_paths.yaml points into this install's tree: the
        // shared root must survive the removal (it would be resurrected by B's
        // startup backfill anyway, making the dialog's promise a lie).
        var shared = new BaseModelFolder { Id = 1, FolderPath = @"C:\AI\ComfyUI\models", InstallerPackageId = 5 };
        var own = new BaseModelFolder { Id = 2, FolderPath = @"C:\AI\ComfyUI\models\extra", InstallerPackageId = 5 };
        var settings = new AppSettings { Id = 1, BaseModelFolders = [shared, own] };

        var plan = InstallationSettingsCleanup.Resolve(
            settings,
            Package(),
            protectedRoots: [@"c:\ai\comfyui\MODELS\"]);

        plan.BaseModelFolders.Should().BeEquivalentTo(new[] { own },
            o => o.WithoutStrictOrdering(),
            "protected roots are matched case-insensitively and separator-tolerantly");
    }

    [Fact]
    public void Resolve_KeepsGallery_AnotherInstallationStillWritesTo()
    {
        // The real case: several ComfyUI installs share one --output-directory while only
        // one of them owns the gallery row's FK. Removing that one must not offer to drop
        // a folder the surviving installs keep filling with images.
        var shared = new ImageGallery { Id = 1, FolderPath = @"E:\AI\comfy_output\", InstallerPackageId = 5 };
        var own = new ImageGallery { Id = 2, FolderPath = @"C:\AI\ComfyUI\output", InstallerPackageId = 5 };
        var settings = new AppSettings { Id = 1, ImageGalleries = [shared, own] };

        var plan = InstallationSettingsCleanup.Resolve(
            settings,
            Package(),
            foldersUsedByOthers: [@"E:\AI\comfy_output"]);

        plan.Galleries.Should().ContainSingle().Which.Should().BeSameAs(own);
        plan.SharedGalleryFolders.Should().ContainSingle().Which.Should().Be(@"E:\AI\comfy_output\",
            "the dialog has to say why the folder is being kept instead of claiming none was linked");
        plan.SharedLoraSourceFolders.Should().BeEmpty("the held-back path belongs to the gallery row only");
        plan.SharedBaseModelFolders.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_KeepsGallery_InASubfolderOfAnotherInstallationsOutputDirectory()
    {
        // ComfyUI writes into dated/prefixed subfolders of its output directory, so a row
        // one level down is just as actively used as the root itself.
        var nested = new ImageGallery { Id = 1, FolderPath = @"E:\AI\comfy_output\2026-08", InstallerPackageId = 5 };
        var settings = new AppSettings { Id = 1, ImageGalleries = [nested] };

        var plan = InstallationSettingsCleanup.Resolve(
            settings,
            Package(),
            foldersUsedByOthers: [@"E:\AI\comfy_output"]);

        plan.Galleries.Should().BeEmpty();
        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Resolve_KeepsLoraSourcesAndBaseFolders_UsedByAnotherInstallation()
    {
        // A nested installation: B lives inside A's tree, so A's removal must not sweep
        // rows that are really B's.
        var lora = new LoraSource { Id = 1, FolderPath = @"C:\AI\ComfyUI\nested\models\loras" };
        var ownLora = new LoraSource { Id = 2, FolderPath = @"C:\AI\ComfyUI\models\loras" };
        var baseFolder = new BaseModelFolder { Id = 1, FolderPath = @"C:\AI\ComfyUI\nested\models", InstallerPackageId = 5 };
        var settings = new AppSettings
        {
            Id = 1,
            LoraSources = [lora, ownLora],
            BaseModelFolders = [baseFolder]
        };

        var plan = InstallationSettingsCleanup.Resolve(
            settings,
            Package(),
            foldersUsedByOthers: [@"C:\AI\ComfyUI\nested"]);

        plan.LoraSources.Should().ContainSingle().Which.Should().BeSameAs(ownLora);
        plan.BaseModelFolders.Should().BeEmpty();
        plan.SharedLoraSourceFolders.Should().BeEquivalentTo(new[] { @"C:\AI\ComfyUI\nested\models\loras" });
        plan.SharedBaseModelFolders.Should().BeEquivalentTo(new[] { @"C:\AI\ComfyUI\nested\models" });
        plan.SharedFolders.Should().BeEquivalentTo(new[]
        {
            @"C:\AI\ComfyUI\nested\models\loras",
            @"C:\AI\ComfyUI\nested\models"
        }, o => o.WithoutStrictOrdering(), "the note lists every held-back path across the three kinds");
    }

    [Fact]
    public void Resolve_WithoutOtherInstallations_OffersEverythingLinked()
    {
        var gallery = new ImageGallery { Id = 1, FolderPath = @"E:\AI\comfy_output\", InstallerPackageId = 5 };
        var settings = new AppSettings { Id = 1, ImageGalleries = [gallery] };

        var plan = InstallationSettingsCleanup.Resolve(settings, Package());

        plan.Galleries.Should().ContainSingle().Which.Should().BeSameAs(gallery,
            "sole ownership is the normal case and must stay offered");
        plan.SharedFolders.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_EmptySettings_YieldsEmptyPlan()
    {
        var plan = InstallationSettingsCleanup.Resolve(new AppSettings { Id = 1 }, Package());

        plan.Galleries.Should().BeEmpty();
        plan.LoraSources.Should().BeEmpty();
        plan.BaseModelFolders.Should().BeEmpty();
        plan.SharedFolders.Should().BeEmpty();
        plan.IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\AI\ComfyUI", true)]
    [InlineData(@"C:\AI\ComfyUI\", true)]
    [InlineData(@"C:\AI\ComfyUI\models\loras", true)]
    [InlineData(@"C:\AI\ComfyUI-Backup\loras", false)]
    [InlineData(@"C:\AI", false)]
    [InlineData(@"D:\Elsewhere", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUnderInstallation_HandlesEdgeCases(string? candidate, bool expected)
    {
        InstallationSettingsCleanup.IsUnderInstallation(candidate, @"C:\AI\ComfyUI")
            .Should().Be(expected);
    }

    [Fact]
    public void IsUnderInstallation_ToleratesInvalidPaths()
    {
        InstallationSettingsCleanup.IsUnderInstallation("\0bad", @"C:\AI\ComfyUI").Should().BeFalse();
        InstallationSettingsCleanup.IsUnderInstallation(@"C:\AI\ComfyUI\models", null).Should().BeFalse();
    }
}
