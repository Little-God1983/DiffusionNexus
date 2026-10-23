using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Engine feature downloads go to the Engine's own models folder (owner ruling 2026-10-12), not to the
/// first library of its extra_model_paths.yaml, which is the user's starred Base Model Folder.
/// </summary>
public sealed class WorkloadInstallServiceModelsRootTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("dn-models-root-");

    public void Dispose() { try { _temp.Delete(recursive: true); } catch { /* best effort */ } }

    private string Repo()
    {
        var repo = Path.Combine(_temp.FullName, "ComfyUI");
        var library = Path.Combine(_temp.FullName, "Library");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(repo, "extra_model_paths.yaml"),
            $"dn_models_1:\n    base_path: {library.Replace('\\', '/')}\n");
        return repo;
    }

    [Fact]
    public void WithoutOverride_TheFirstYamlLibraryWins_AsBefore()
    {
        var repo = Repo();
        WorkloadInstallService.ResolveModelBaseFolder(repo, modelsRootOverride: null)
            .Should().Be(Path.Combine(_temp.FullName, "Library"));
    }

    [Fact]
    public void WithOverride_TheOverrideWins_AndAModelLandsInItsCategory()
    {
        var repo = Repo();
        var models = Path.Combine(repo, "models");

        var baseFolder = WorkloadInstallService.ResolveModelBaseFolder(repo, models);
        var destination = ModelDestinationResolver.Resolve(
            new InstallationConfiguration(), new ModelDownload { Name = "vae", Destination = "models/vae" }, repo, baseFolder);

        baseFolder.Should().Be(models);
        destination.Should().Be(Path.Combine(models, "vae"));
    }
}
