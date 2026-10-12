using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.Diffusion;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Covers <see cref="ModelFolderCatalog"/>: download-target ordering (default first),
/// the LocalAppData fallback when no Base Model Folders are configured, search-root
/// dedupe/existence filtering, and the fallback on an uncreatable default.
/// </summary>
public sealed class ModelFolderCatalogTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("dn-model-folder-catalog-");
    private readonly Mock<IAppSettingsService> _settings = new();

    public void Dispose()
    {
        try { _tempDir.Delete(recursive: true); } catch { /* best effort */ }
    }

    private string Dir(string name, bool create = true)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        if (create) Directory.CreateDirectory(path);
        return path;
    }

    private ModelFolderCatalog CreateSut(params BaseModelFolder[] enabledFolders)
    {
        _settings
            .Setup(s => s.GetEnabledBaseModelFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(enabledFolders.ToList());
        return new ModelFolderCatalog(_settings.Object);
    }

    [Fact]
    public async Task DownloadTargets_FallbackOnly_WhenNoFoldersConfigured()
    {
        var sut = CreateSut();

        var targets = await sut.GetDownloadTargetsAsync();

        var target = targets.Should().ContainSingle().Subject;
        target.Path.Should().Be(ModelFolderCatalog.FallbackRoot);
        target.IsDefault.Should().BeTrue("the fallback is the only choice, so it is the default");
    }

    [Fact]
    public async Task DownloadTargets_DefaultFolderFirst_ThenByOrder_ThenAppFolder()
    {
        var a = Dir("A");
        var b = Dir("B");
        var sut = CreateSut(
            new BaseModelFolder { FolderPath = a, Order = 0 },
            new BaseModelFolder { FolderPath = b, Order = 1, IsDefault = true });

        var targets = await sut.GetDownloadTargetsAsync();

        targets.Select(t => t.Path).Should().Equal(b, a, ModelFolderCatalog.FallbackRoot);
        targets[0].IsDefault.Should().BeTrue();
        targets[1].IsDefault.Should().BeFalse();
        targets[2].IsDefault.Should().BeFalse(
            "the app folder is always selectable but only the default when nothing else exists");
    }

    [Fact]
    public async Task DownloadTargets_DoNotDuplicateTheAppFolder_WhenItIsAlsoARegisteredRow()
    {
        var sut = CreateSut(
            new BaseModelFolder { FolderPath = ModelFolderCatalog.FallbackRoot.ToUpperInvariant(), Order = 0 });

        var targets = await sut.GetDownloadTargetsAsync();

        targets.Should().ContainSingle();
    }

    [Fact]
    public async Task DownloadTargets_FirstEnabledIsDefault_WhenNoRowIsFlagged()
    {
        var a = Dir("A");
        var b = Dir("B");
        var sut = CreateSut(
            new BaseModelFolder { FolderPath = a, Order = 0 },
            new BaseModelFolder { FolderPath = b, Order = 1 });

        var targets = await sut.GetDownloadTargetsAsync();

        targets[0].Path.Should().Be(a);
        targets[0].IsDefault.Should().BeTrue();
    }

    // C2: the Engine's models folder is a registered Base Model Folder (linked to the Engine row, first in
    // order). Core downloads must never land there: Uninstall Engine deletes everything inside the Engine.
    private ModelFolderCatalog CreateSutWithEngine(string engineRoot, params BaseModelFolder[] enabledFolders)
    {
        _settings
            .Setup(s => s.GetEnabledBaseModelFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(enabledFolders.ToList());
        var packages = new Mock<IInstallerPackageRepository>();
        packages.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new InstallerPackage { Id = 7, Name = "Diffusion Nexus Engine", InstallationPath = engineRoot, IsAppManaged = true, ExecutablePath = string.Empty },
            new InstallerPackage { Id = 8, Name = "My ComfyUI", InstallationPath = Dir("MyComfy"), IsAppManaged = false, ExecutablePath = string.Empty }
        ]);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.InstallerPackages).Returns(packages.Object);
        return new ModelFolderCatalog(_settings.Object, unitOfWork.Object);
    }

    [Fact]
    public async Task DownloadTargets_OnlyTheEngineFolderRegistered_DefaultIsTheAppFolder()
    {
        var engine = Dir("Engine");
        var sut = CreateSutWithEngine(engine,
            new BaseModelFolder { FolderPath = Path.Combine(engine, "ComfyUI", "models"), Order = 0, InstallerPackageId = 7 });

        var targets = await sut.GetDownloadTargetsAsync();

        var target = targets.Should().ContainSingle().Subject;
        target.Path.Should().Be(ModelFolderCatalog.FallbackRoot);
        target.IsDefault.Should().BeTrue();
        (await sut.GetDefaultDownloadRootAsync()).Should().Be(ModelFolderCatalog.FallbackRoot);
    }

    [Fact]
    public async Task DownloadTargets_EngineFolderAndAUserFolder_TheUserFolderIsTheDefault_TheEngineFolderIsNotOffered()
    {
        var engine = Dir("Engine");
        var engineModels = Path.Combine(engine, "ComfyUI", "models");
        var user = Dir("UserModels");
        var otherComfyModels = Path.Combine(Dir("MyComfy"), "models");
        var sut = CreateSutWithEngine(engine,
            new BaseModelFolder { FolderPath = engineModels, Order = 0, InstallerPackageId = 7 },
            new BaseModelFolder { FolderPath = user, Order = 1 },
            new BaseModelFolder { FolderPath = otherComfyModels, Order = 2, InstallerPackageId = 8 });

        var targets = await sut.GetDownloadTargetsAsync();

        targets.Select(t => t.Path).Should().Equal(user, otherComfyModels, ModelFolderCatalog.FallbackRoot);
        targets[0].IsDefault.Should().BeTrue();
    }

    // A starred row inside the Engine is still never a download target.
    [Fact]
    public async Task DownloadTargets_AStarredFolderInsideTheEngine_IsNotOffered()
    {
        var engine = Dir("Engine");
        var user = Dir("UserModels");
        var sut = CreateSutWithEngine(engine,
            new BaseModelFolder { FolderPath = Path.Combine(engine, "models"), Order = 0, IsDefault = true },
            new BaseModelFolder { FolderPath = user, Order = 1 });

        var targets = await sut.GetDownloadTargetsAsync();

        targets.Select(t => t.Path).Should().Equal(user, ModelFolderCatalog.FallbackRoot);
        targets[0].IsDefault.Should().BeTrue();
    }

    // By path, never by link: an old startup backfill linked the user's own library to the Engine row.
    [Fact]
    public async Task DownloadTargets_AUserFolderLinkedToTheEngineRow_IsStillOffered()
    {
        var engine = Dir("Engine");
        var user = Dir("UserModels");
        var sut = CreateSutWithEngine(engine, new BaseModelFolder { FolderPath = user, Order = 0, IsDefault = true, InstallerPackageId = 7 });

        var targets = await sut.GetDownloadTargetsAsync();

        targets[0].Path.Should().Be(user);
        targets[0].IsDefault.Should().BeTrue();
    }

    // The app still reads the Engine's models: only where Core downloads go changes.
    [Fact]
    public async Task SearchRoots_StillIncludeTheEngineFolder()
    {
        var engine = Dir("Engine");
        var engineModels = Dir(Path.Combine("Engine", "models"));
        var sut = CreateSutWithEngine(engine, new BaseModelFolder { FolderPath = engineModels, Order = 0, InstallerPackageId = 7 });

        (await sut.GetSearchRootsAsync()).Should().Contain(engineModels);
    }

    // The catalog is resolved once, so its unit of work is one long-lived DbContext: EF throws when a second
    // operation starts on it. Two concurrent calls must both read the packages and both leave the Engine out.
    [Fact]
    public async Task DownloadTargets_TwoConcurrentCalls_BothLeaveTheEngineFolderOut()
    {
        var engine = Dir("Engine");
        var user = Dir("UserModels");
        _settings
            .Setup(s => s.GetEnabledBaseModelFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new BaseModelFolder { FolderPath = Path.Combine(engine, "models"), Order = 0, InstallerPackageId = 7 },
                new BaseModelFolder { FolderPath = user, Order = 1 }]);
        var inside = 0;
        var packages = new Mock<IInstallerPackageRepository>();
        packages.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            if (Interlocked.Increment(ref inside) > 1)
            {
                Interlocked.Decrement(ref inside);
                throw new InvalidOperationException("A second operation was started on this context instance before a previous operation completed.");
            }
            try
            {
                await Task.Delay(100);
                return (IReadOnlyList<InstallerPackage>)
                [
                    new InstallerPackage { Id = 7, Name = "Diffusion Nexus Engine", InstallationPath = engine, IsAppManaged = true, ExecutablePath = string.Empty }
                ];
            }
            finally { Interlocked.Decrement(ref inside); }
        });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.InstallerPackages).Returns(packages.Object);
        var sut = new ModelFolderCatalog(_settings.Object, unitOfWork.Object);

        var results = await Task.WhenAll(
            Task.Run(() => sut.GetDownloadTargetsAsync()),
            Task.Run(() => sut.GetDownloadTargetsAsync()));

        foreach (var targets in results)
            targets.Select(t => t.Path).Should().Equal(user, ModelFolderCatalog.FallbackRoot);
    }

    // Runtime checks fall through: a failing package read leaves the targets as they were.
    [Fact]
    public async Task DownloadTargets_PackagesCannotBeRead_AllFoldersAreOffered()
    {
        var a = Dir("A");
        _settings
            .Setup(s => s.GetEnabledBaseModelFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BaseModelFolder { FolderPath = a, Order = 0 }]);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.InstallerPackages.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var sut = new ModelFolderCatalog(_settings.Object, unitOfWork.Object);

        var targets = await sut.GetDownloadTargetsAsync();

        targets[0].Path.Should().Be(a);
    }

    [Fact]
    public async Task GetDefaultDownloadRoot_CreatesTheDirectory()
    {
        var missing = Dir("NotYetCreated", create: false);
        var sut = CreateSut(new BaseModelFolder { FolderPath = missing, IsDefault = true });

        var root = await sut.GetDefaultDownloadRootAsync();

        root.Should().Be(missing);
        Directory.Exists(missing).Should().BeTrue();
    }

    [Fact]
    public async Task GetDefaultDownloadRoot_FallsBack_WhenDefaultIsUncreatable()
    {
        var invalid = Path.Combine(_tempDir.FullName, "bad\0path");
        var sut = CreateSut(new BaseModelFolder { FolderPath = invalid, IsDefault = true });

        var root = await sut.GetDefaultDownloadRootAsync();

        root.Should().Be(ModelFolderCatalog.FallbackRoot);
    }

    [Fact]
    public async Task SearchRoots_SkipMissingDirs_AndDedupeCaseInsensitively()
    {
        var a = Dir("A");
        var missing = Dir("Gone", create: false);
        var sut = CreateSut(
            new BaseModelFolder { FolderPath = a, Order = 0 },
            new BaseModelFolder { FolderPath = a.ToUpperInvariant(), Order = 1 },
            new BaseModelFolder { FolderPath = missing, Order = 2 });

        var roots = await sut.GetSearchRootsAsync();

        roots.Should().ContainSingle(r => string.Equals(r, a, StringComparison.OrdinalIgnoreCase));
        roots.Should().NotContain(missing);
    }
}
