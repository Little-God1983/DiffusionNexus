using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

/// <summary>
/// Covers <see cref="EngineModelPathsSynchronizer"/>: the engine ends up reading every registered
/// model folder, with each folder's own category mapping.
///
/// <para>
/// The bug this pins down: the engine's file was written once at install time from the SDK's
/// single-valued <c>ModelBaseFolder</c>, so only the first registered folder was ever declared. A
/// user with four folders whose models all lived in the third got an engine that could not load
/// any of them and a workload dialog reporting "None" — correctly, which is what made it hard to
/// read as a wiring bug rather than a missing download.
/// </para>
/// </summary>
public sealed class EngineModelPathsSynchronizerTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("dn-engine-paths-");

    public void Dispose()
    {
        try { _temp.Delete(recursive: true); } catch { /* best effort */ }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_temp.FullName, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A ComfyUI install: main.py in the root, plus a models/ folder.</summary>
    private string CreateComfyInstall(string name)
    {
        var root = Dir(name);
        File.WriteAllText(Path.Combine(root, "main.py"), "# comfy");
        Dir(name, "models");
        return root;
    }

    private static InstallerPackage Package(int id, string path, bool isAppManaged = false)
        => new()
        {
            Id = id,
            Name = isAppManaged ? "Diffusion Nexus Engine" : "ComfyUI",
            InstallationPath = path,
            ExecutablePath = "run.bat",
            Type = InstallerType.ComfyUI,
            IsAppManaged = isAppManaged
        };

    private static EngineModelPathsSynchronizer Sut(
        IReadOnlyList<InstallerPackage> packages,
        IReadOnlyList<string> searchRoots)
    {
        var repo = new Mock<IInstallerPackageRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(packages);

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.InstallerPackages).Returns(repo.Object);

        var catalog = new Mock<IModelFolderCatalog>();
        catalog.Setup(c => c.GetSearchRootsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(searchRoots);

        return new EngineModelPathsSynchronizer(uow.Object, catalog.Object);
    }

    private static IReadOnlyList<ComfyExtraModelPathsSection> Written(string filePath)
        => ComfyExtraModelPaths.Parse(File.ReadAllLines(filePath));

    [Fact]
    public async Task Sync_WiresEveryRegisteredFolder_NotJustTheFirst()
    {
        var engine = CreateComfyInstall("Engine");
        var libraryA = Dir("LibraryA");
        var libraryB = Dir("LibraryB");

        var result = await Sut([Package(1, engine, isAppManaged: true)], [libraryA, libraryB])
            .SyncAsync();

        result.Written.Should().BeTrue();
        result.Roots.Should().Equal(libraryA, libraryB);
        Written(result.FilePath!).Select(s => s.BasePath)
            .Should().Equal(libraryA.Replace('\\', '/'), libraryB.Replace('\\', '/'));
    }

    [Fact]
    public async Task Sync_CopiesTheCategoryMappingOfTheInstallThatDeclaredTheLibrary()
    {
        // The real-world case: a shared library whose folders are named TextEncoders/, ESRGAN/,
        // DiffusionModels/ — declared that way in the user's own ComfyUI's yaml. Wiring the engine
        // with ComfyUI's default names would point it at folders that do not exist.
        var engine = CreateComfyInstall("Engine");
        var userComfy = CreateComfyInstall("UserComfy");
        var library = Dir("SharedLibrary");

        File.WriteAllLines(Path.Combine(userComfy, ComfyExtraModelPaths.FileName),
        [
            "comfyui:",
            $"    base_path: {library}",
            "    text_encoders: TextEncoders/",
            "    upscale_models: ESRGAN/",
        ]);

        var result = await Sut(
                [Package(1, engine, isAppManaged: true), Package(2, userComfy)],
                [library])
            .SyncAsync();

        var section = Written(result.FilePath!).Single();
        section.Categories.Should().Equal(
            new ComfyCategoryPath("text_encoders", "TextEncoders/"),
            new ComfyCategoryPath("upscale_models", "ESRGAN/"));
    }

    [Fact]
    public async Task Sync_PlainModelsFolder_GetsTheStandardCategoryNames()
    {
        var engine = CreateComfyInstall("Engine");
        var userComfy = CreateComfyInstall("UserComfy");
        var userModels = Path.Combine(userComfy, "models");

        var result = await Sut(
                [Package(1, engine, isAppManaged: true), Package(2, userComfy)],
                [userModels])
            .SyncAsync();

        Written(result.FilePath!).Single().Categories.Select(c => c.Category)
            .Should().BeEquivalentTo(EngineModelPathsFile.DefaultCategories);
    }

    [Fact]
    public async Task Sync_SkipsTheEnginesOwnModelsFolder()
    {
        // It is registered like any other folder, but ComfyUI already searches it by default.
        var engine = CreateComfyInstall("Engine");
        var engineModels = Path.Combine(engine, "models");
        var library = Dir("Library");

        var result = await Sut([Package(1, engine, isAppManaged: true)], [engineModels, library])
            .SyncAsync();

        result.Roots.Should().Equal(library);
    }

    [Fact]
    public async Task Sync_IgnoresTheEnginesOwnYaml_WhenLookingUpMappings()
    {
        // Reading back the file we are about to write would make the mapping self-referential and
        // freeze whatever the first version said in place forever.
        var engine = CreateComfyInstall("Engine");
        var library = Dir("Library");

        File.WriteAllLines(Path.Combine(engine, ComfyExtraModelPaths.FileName),
        [
            "dn_models_1:",
            $"    base_path: {library}",
            "    loras: WrongFolderFromAnEarlierRun/",
        ]);

        var result = await Sut([Package(1, engine, isAppManaged: true)], [library]).SyncAsync();

        Written(result.FilePath!).Single().Categories
            .Should().NotContain(c => c.Value == "WrongFolderFromAnEarlierRun/")
            .And.Contain(new ComfyCategoryPath("loras", "loras/"));
    }

    [Fact]
    public async Task Sync_UnchangedContent_DoesNotRewriteTheFile()
    {
        var engine = CreateComfyInstall("Engine");
        var library = Dir("Library");
        var sut = Sut([Package(1, engine, isAppManaged: true)], [library]);

        var first = await sut.SyncAsync();
        var second = await sut.SyncAsync();

        first.Written.Should().BeTrue();
        second.Written.Should().BeFalse("nothing changed, so the file must be left alone");
        second.Roots.Should().Equal(library);
    }

    [Fact]
    public async Task Sync_ExplicitInstallRoot_IsUsedEvenWithoutADatabaseRow()
    {
        // Straight after an install the row may not be visible yet, so the caller passes the path.
        var engine = CreateComfyInstall("Engine");
        var library = Dir("Library");

        var result = await Sut([], [library]).SyncAsync(engine);

        result.Written.Should().BeTrue();
        result.FilePath.Should().Be(Path.Combine(engine, ComfyExtraModelPaths.FileName));
    }

    [Fact]
    public async Task Sync_NoEngineInstalled_DoesNothing()
    {
        var result = await Sut([], [Dir("Library")]).SyncAsync();

        result.Written.Should().BeFalse();
        result.SkipReason.Should().Contain("not installed");
    }

    [Fact]
    public async Task Sync_EngineFolderWithoutMainPy_DoesNothing()
    {
        var ghost = Dir("Ghost");

        var result = await Sut([Package(1, ghost, isAppManaged: true)], [Dir("Library")]).SyncAsync();

        result.Written.Should().BeFalse();
        result.SkipReason.Should().Contain("entry point");
    }

    [Fact]
    public async Task Sync_NoRegisteredFolders_LeavesTheExistingFileAlone()
    {
        // Truncating to a header would be worse than a stale file: the engine's own models/ folder
        // still works, and an empty registry is a transient startup state.
        var engine = CreateComfyInstall("Engine");
        var filePath = Path.Combine(engine, ComfyExtraModelPaths.FileName);
        File.WriteAllText(filePath, "comfyui:\n    base_path: D:/Models/\n");

        var result = await Sut([Package(1, engine, isAppManaged: true)], []).SyncAsync();

        result.Written.Should().BeFalse();
        result.SkipReason.Should().Contain("no model folders");
        File.ReadAllText(filePath).Should().Contain("D:/Models/");
    }

    [Fact]
    public async Task Sync_RepositoryFailure_IsSwallowed()
    {
        // Called on every engine start and during startup — it must never take the app down.
        var repo = new Mock<IInstallerPackageRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.InstallerPackages).Returns(repo.Object);

        var sut = new EngineModelPathsSynchronizer(uow.Object, Mock.Of<IModelFolderCatalog>());

        var result = await sut.SyncAsync();

        result.Written.Should().BeFalse();
        result.SkipReason.Should().Be("db down");
    }
}
