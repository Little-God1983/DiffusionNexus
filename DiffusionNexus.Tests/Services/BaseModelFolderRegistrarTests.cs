using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.Diffusion;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Covers <see cref="BaseModelFolderRegistrar"/>: which model roots get auto-registered
/// per installation type (ComfyUI via ComfyUiPathDiscovery — including
/// extra_model_paths.yaml roots — other types via their plain models/ folder),
/// idempotency, and the package link.
/// </summary>
public sealed class BaseModelFolderRegistrarTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("dn-bmf-registrar-");

    public void Dispose()
    {
        try { _tempDir.Delete(recursive: true); } catch { /* best effort */ }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_tempDir.FullName, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static InstallerPackage Package(string installPath, InstallerType type, int id = 5)
        => new() { Id = id, Name = "Test", InstallationPath = installPath, ExecutablePath = "run.bat", Type = type };

    [Fact]
    public void ResolveModelRoots_ComfyUI_IncludesModelsDir_And_ExtraModelPathsYamlRoots()
    {
        // Manual ComfyUI layout: root contains main.py + models/.
        var install = Dir("Comfy");
        File.WriteAllText(Path.Combine(install, "main.py"), "# comfy");
        var modelsDir = Dir("Comfy", "models");

        // extra_model_paths.yaml declaring a shared library that exists.
        var shared = Dir("SharedLibrary");
        File.WriteAllText(Path.Combine(install, "extra_model_paths.yaml"),
            $"library:\n  base_path: {shared}\n");

        var roots = BaseModelFolderRegistrar.ResolveModelRoots(
            Package(install, InstallerType.ComfyUI));

        roots.Should().Contain(modelsDir);
        roots.Should().Contain(shared, "extra_model_paths.yaml roots must be registered too");
    }

    [Fact]
    public void ResolveModelRoots_NonComfyUI_ReturnsModelsSubfolder_WhenPresent()
    {
        var install = Dir("Forge");
        var modelsDir = Dir("Forge", "models");

        var roots = BaseModelFolderRegistrar.ResolveModelRoots(
            Package(install, InstallerType.Forge));

        roots.Should().Equal(modelsDir);
    }

    [Fact]
    public void ResolveModelRoots_ReturnsEmpty_ForInvalidInstallationPath()
    {
        var roots = BaseModelFolderRegistrar.ResolveModelRoots(
            Package(Path.Combine(_tempDir.FullName, "does-not-exist"), InstallerType.ComfyUI));

        roots.Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterPackageFolders_RegistersEachRoot_AndReportsHowManyWereNew()
    {
        var install = Dir("Forge");
        var modelsDir = Dir("Forge", "models");
        var settings = new Mock<IAppSettingsService>();
        // First call inserts a new row, the second is a no-op (path already registered).
        settings
            .SetupSequence(s => s.AddBaseModelFolderAsync(modelsDir, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        var sut = new BaseModelFolderRegistrar(settings.Object);

        var firstAdded = await sut.RegisterPackageFoldersAsync(Package(install, InstallerType.Forge, id: 42));
        var secondAdded = await sut.RegisterPackageFoldersAsync(Package(install, InstallerType.Forge, id: 42));

        // Idempotency lives in IAppSettingsService.AddBaseModelFolderAsync (path-unique);
        // the registrar funnels every root through it and reports how many were new so
        // callers know whether to announce a settings change (Settings page reload).
        firstAdded.Should().Be(1);
        secondAdded.Should().Be(0);
        settings.Verify(
            s => s.AddBaseModelFolderAsync(modelsDir, 42, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task EnsureRegistered_SwallowsFailures_AndProcessesAllPackages()
    {
        var install = Dir("Forge");
        var modelsDir = Dir("Forge", "models");

        var settings = new Mock<IAppSettingsService>();
        settings
            .Setup(s => s.AddBaseModelFolderAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var sut = new BaseModelFolderRegistrar(settings.Object);

        var added = 0;
        var act = async () => added = await sut.EnsureRegisteredAsync(
        [
            Package(install, InstallerType.Forge, id: 1),
            Package(install, InstallerType.Forge, id: 2),
        ]);

        await act.Should().NotThrowAsync("startup backfill must never break app startup");
        added.Should().Be(0, "failed registrations must not count as added");
        settings.Verify(
            s => s.AddBaseModelFolderAsync(modelsDir, It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public void ResolveModelRoots_ComfyUI_ExcludesPerCategoryFoldersFromExtraModelPaths()
    {
        // The bug: every category entry in extra_model_paths.yaml was registered as a root,
        // so one yaml produced a Settings list of twenty folders where one was correct.
        var install = Dir("Comfy");
        File.WriteAllText(Path.Combine(install, "main.py"), "# comfy");
        var modelsDir = Dir("Comfy", "models");

        var library = Dir("Library");
        var loras = Dir("Library", "Lora");
        var vae = Dir("Library", "VAE");
        File.WriteAllLines(Path.Combine(install, "extra_model_paths.yaml"),
        [
            "comfyui:",
            $"    base_path: {library}",
            "    loras: Lora/",
            "    vae: VAE/",
        ]);

        var roots = BaseModelFolderRegistrar.ResolveModelRoots(
            Package(install, InstallerType.ComfyUI));

        roots.Should().BeEquivalentTo(new[] { modelsDir, library },
            o => o.WithoutStrictOrdering(),
            "only the installation's models/ folder and the shared base_path are roots");
        roots.Should().NotContain(loras).And.NotContain(vae);
    }

    [Fact]
    public async Task PruneRedundantFolders_RemovesTheCategoryRows_AndKeepsTheRoots()
    {
        var install = Dir("Comfy");
        File.WriteAllText(Path.Combine(install, "main.py"), "# comfy");
        var modelsDir = Dir("Comfy", "models");
        var library = Dir("Library");
        var loras = Dir("Library", "Lora");
        File.WriteAllLines(Path.Combine(install, "extra_model_paths.yaml"),
        [
            "comfyui:",
            $"    base_path: {library}",
            "    loras: Lora/",
        ]);

        var package = Package(install, InstallerType.ComfyUI, id: 6);
        BaseModelFolder[] rows =
        [
            new() { Id = 1, FolderPath = library, InstallerPackageId = 6 },
            new() { Id = 2, FolderPath = loras, InstallerPackageId = 6 },
            new() { Id = 3, FolderPath = modelsDir, InstallerPackageId = 6 },
        ];

        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetAllBaseModelFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);
        IReadOnlyCollection<int>? removedIds = null;
        settings.Setup(s => s.RemoveBaseModelFoldersAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<int>, CancellationToken>((ids, _) => removedIds = ids)
            .ReturnsAsync(1);

        var removed = await new BaseModelFolderRegistrar(settings.Object)
            .PruneRedundantFoldersAsync([package]);

        removed.Should().Be(1);
        removedIds.Should().BeEquivalentTo(new[] { 2 }, "only the loras category row is redundant");
    }

    [Fact]
    public async Task PruneRedundantFolders_NothingRedundant_DoesNotCallRemove()
    {
        var install = Dir("Forge");
        Dir("Forge", "models");
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetAllBaseModelFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BaseModelFolder { Id = 1, FolderPath = Dir("Forge", "models"), InstallerPackageId = 1 }]);

        var removed = await new BaseModelFolderRegistrar(settings.Object)
            .PruneRedundantFoldersAsync([Package(install, InstallerType.Forge, id: 1)]);

        removed.Should().Be(0);
        settings.Verify(
            s => s.RemoveBaseModelFoldersAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()),
            Times.Never, "an empty prune must not issue a write");
    }

    [Fact]
    public async Task PruneRedundantFolders_SwallowsFailures()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetAllBaseModelFoldersAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var removed = 0;
        var act = async () => removed = await new BaseModelFolderRegistrar(settings.Object)
            .PruneRedundantFoldersAsync([Package(Dir("Forge"), InstallerType.Forge)]);

        await act.Should().NotThrowAsync("a cleanup failure must never break app startup");
        removed.Should().Be(0);
    }
}
