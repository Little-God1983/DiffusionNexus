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
    public async Task RegisterPackageFolders_RegistersEachRoot_WithPackageLink()
    {
        var install = Dir("Forge");
        var modelsDir = Dir("Forge", "models");
        var settings = new Mock<IAppSettingsService>();
        var sut = new BaseModelFolderRegistrar(settings.Object);

        await sut.RegisterPackageFoldersAsync(Package(install, InstallerType.Forge, id: 42));
        await sut.RegisterPackageFoldersAsync(Package(install, InstallerType.Forge, id: 42));

        // Idempotency lives in IAppSettingsService.AddBaseModelFolderAsync (path-unique);
        // the registrar simply funnels every root through it with the package id.
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

        var act = async () => await sut.EnsureRegisteredAsync(
        [
            Package(install, InstallerType.Forge, id: 1),
            Package(install, InstallerType.Forge, id: 2),
        ]);

        await act.Should().NotThrowAsync("startup backfill must never break app startup");
        settings.Verify(
            s => s.AddBaseModelFolderAsync(modelsDir, It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
