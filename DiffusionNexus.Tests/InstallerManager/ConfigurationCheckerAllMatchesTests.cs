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

    /// <summary>
    /// An Engine whose yaml lists a category folder of the user's library, where another ControlNet keeps
    /// a file with the same generic HuggingFace name the feature's URL has.
    /// </summary>
    private (string Repo, string GenericInLibrary, string ConfiguredInEngine) GenericNamedLibraryFile()
    {
        var repo = Path.Combine(_temp.FullName, "Engine");
        var library = Path.Combine(_temp.FullName, "Library");
        Directory.CreateDirectory(Path.Combine(repo, "models", "controlnet"));
        Directory.CreateDirectory(Path.Combine(library, "controlnet", "OtherNet"));
        File.WriteAllText(Path.Combine(repo, "main.py"), "");
        File.WriteAllText(Path.Combine(repo, "extra_model_paths.yaml"),
            $"dn_models_1:\n    base_path: {library.Replace('\\', '/')}\n    controlnet: controlnet/\n");
        var generic = Path.Combine(library, "controlnet", "OtherNet", "diffusion_pytorch_model.safetensors");
        var configured = Path.Combine(repo, "models", "controlnet", "InstantX-Union.safetensors");
        File.WriteAllText(generic, "other");
        return (repo, generic, configured);
    }

    private static InstallationConfiguration GenericNameConfig()
    {
        var config = new InstallationConfiguration { Name = "t" };
        config.ModelDownloads.Add(new ModelDownload
        {
            Name = "InstantX-Union", Destination = "models/controlnet",
            DownloadLinks = [new ModelDownloadLink { Url = "https://huggingface.co/x/resolve/main/diffusion_pytorch_model.safetensors" }]
        });
        return config;
    }

    [Fact]
    public async Task CollectAllMatches_SearchesTheConfiguredName_NotTheGenericUrlName()
    {
        var (repo, generic, configured) = GenericNamedLibraryFile();
        File.WriteAllText(configured, "x");

        var result = await new ConfigurationCheckerService().CheckConfigurationAsync(
            GenericNameConfig(), repo, new ConfigurationCheckOptions { CollectAllMatches = true });

        var model = result.ModelResults.Single();
        model.AllFoundPaths.Should().Equal([configured],
            "the installer renames the download to its configured name, so a generic-named file elsewhere is someone else's");
        model.AllFoundPaths.Should().NotContain(generic);
    }

    [Fact]
    public async Task Default_StillFindsTheGenericUrlName()
    {
        var (repo, generic, _) = GenericNamedLibraryFile();

        var result = await new ConfigurationCheckerService().CheckConfigurationAsync(GenericNameConfig(), repo);

        var model = result.ModelResults.Single();
        model.IsInstalled.Should().BeTrue();
        model.FoundAtPath.Should().Be(generic);
    }

    // A download URL without a file name (Civitai's …/models/3139172?fileId=…) gives no name to search for:
    // the file is saved under the server's name, so not finding it says nothing.
    [Fact]
    public async Task AModelWhoseUrlCarriesNoFileName_IsNotFindableByName()
    {
        var (repo, _, _) = TwoCopies();
        var config = Config();
        config.ModelDownloads.Add(new ModelDownload
        {
            Name = "Krea 2 Identity Edit", Destination = "models/loras",
            DownloadLinks = [new ModelDownloadLink { Url = "https://civitai.com/api/download/models/3139172?fileId=3019297" }]
        });

        var result = await new ConfigurationCheckerService().CheckConfigurationAsync(
            config, repo, new ConfigurationCheckOptions { CollectAllMatches = true });

        result.ModelResults.Single(m => m.Name == "ae").IsFindableByName.Should().BeTrue();
        var lora = result.ModelResults.Single(m => m.Name == "Krea 2 Identity Edit");
        lora.IsInstalled.Should().BeFalse();
        lora.IsFindableByName.Should().BeFalse();
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
