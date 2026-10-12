using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>Removal must see every copy of a model, not only the first one the check finds.</summary>
public sealed class ConfigurationCheckerAllMatchesTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("dn-checker-all-");

    public void Dispose() { try { _temp.Delete(recursive: true); } catch { /* best effort */ } }

    private (string Repo, string EngineCopy, string LibraryCopy) TwoCopies()
    {
        var repo = Path.Combine(_temp.FullName, "Engine");
        var library = Path.Combine(_temp.FullName, "Library");
        Directory.CreateDirectory(Path.Combine(repo, "models", "vae"));
        Directory.CreateDirectory(Path.Combine(library, "vae"));
        File.WriteAllText(Path.Combine(repo, "main.py"), "");
        File.WriteAllText(Path.Combine(repo, "extra_model_paths.yaml"),
            $"dn_models_1:\n    base_path: {library.Replace('\\', '/')}\n");
        var engineCopy = Path.Combine(repo, "models", "vae", "ae.safetensors");
        var libraryCopy = Path.Combine(library, "vae", "ae.safetensors");
        File.WriteAllText(engineCopy, "x");
        File.WriteAllText(libraryCopy, "x");
        return (repo, engineCopy, libraryCopy);
    }

    private static InstallationConfiguration Config()
    {
        var config = new InstallationConfiguration { Name = "t" };
        config.ModelDownloads.Add(new ModelDownload
        {
            Name = "ae", Destination = "models/vae",
            DownloadLinks = [new ModelDownloadLink { Url = "https://example.com/ae.safetensors" }]
        });
        return config;
    }

    [Fact]
    public async Task CollectAllMatches_FindsTheEngineCopy_AndTheLibraryCopy()
    {
        var (repo, engineCopy, libraryCopy) = TwoCopies();

        var result = await new ConfigurationCheckerService().CheckConfigurationAsync(
            Config(), repo, new ConfigurationCheckOptions { CollectAllMatches = true });

        var model = result.ModelResults.Single();
        model.AllFoundPaths.Should().BeEquivalentTo([engineCopy, libraryCopy]);
        model.FoundAtPath.Should().Be(model.AllFoundPaths[0]);
    }

    [Fact]
    public async Task Default_StopsAtTheFirstCopy()
    {
        var (repo, engineCopy, _) = TwoCopies();

        var result = await new ConfigurationCheckerService().CheckConfigurationAsync(Config(), repo);

        var model = result.ModelResults.Single();
        model.FoundAtPath.Should().Be(engineCopy, "the explicit destination is searched first");
        model.AllFoundPaths.Should().Equal(engineCopy);
    }
}
