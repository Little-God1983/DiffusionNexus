using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Covers <see cref="ComfyUiPathDiscovery.EnumerateModelRoots"/> against its sibling
/// <c>EnumerateModelSearchPaths</c>. The two answer different questions and the difference
/// is the whole point: searching has to look inside every per-category folder, while the
/// Base Model Folder registry wants only directories that HOLD those category folders.
/// Conflating them turned one extra_model_paths.yaml into twenty Settings rows.
/// </summary>
public sealed class ComfyUiModelRootsTests : IDisposable
{
    private readonly DirectoryInfo _tempRoot = Directory.CreateTempSubdirectory("dn-comfy-roots-");

    public void Dispose()
    {
        try { _tempRoot.Delete(recursive: true); } catch { /* best-effort */ }
    }

    private string Dir(params string[] segments)
    {
        var path = Path.Combine([_tempRoot.FullName, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A manual ComfyUI install (main.py + models/) with a shared library yaml.</summary>
    private (string Install, string Library, string LorasCategory, string VaeCategory) CreateInstallWithSharedLibrary()
    {
        var install = Dir("Comfy");
        File.WriteAllText(Path.Combine(install, "main.py"), "# comfy");
        Dir("Comfy", "models");

        var library = Dir("Library");
        var loras = Dir("Library", "Lora");
        var vae = Dir("Library", "VAE");

        // Shaped like the real file: a base_path plus per-category entries relative to it.
        File.WriteAllLines(Path.Combine(install, "extra_model_paths.yaml"),
        [
            "comfyui:",
            $"    base_path: {library}",
            "    loras: Lora/",
            "    vae: VAE/",
        ]);

        return (install, library, loras, vae);
    }

    [Fact]
    public void EnumerateModelRoots_ReturnsTheBasePath_ButNotItsCategoryFolders()
    {
        var (install, library, loras, vae) = CreateInstallWithSharedLibrary();

        var roots = ComfyUiPathDiscovery.EnumerateModelRoots(install);

        roots.Should().Contain(library, "a base_path holds the category subfolders — it is a root");
        roots.Should().Contain(Path.Combine(install, "models"));
        roots.Should().NotContain(loras, "D:\\Models\\Lora is the loras category, not a model root");
        roots.Should().NotContain(vae);
    }

    [Fact]
    public void EnumerateModelSearchPaths_StillReturnsTheCategoryFolders()
    {
        // The search-path list must keep its old, wider behaviour: the configuration
        // checker looks for a model file inside each of these.
        var (install, library, loras, vae) = CreateInstallWithSharedLibrary();

        var searchPaths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(install);

        searchPaths.Should().Contain(library);
        searchPaths.Should().Contain(loras);
        searchPaths.Should().Contain(vae);
        searchPaths.Should().HaveCountGreaterThan(
            ComfyUiPathDiscovery.EnumerateModelRoots(install).Count,
            "searching is deliberately broader than the root registry");
    }

    [Fact]
    public void EnumerateModelRoots_AbsoluteCategoryPaths_AreAlsoExcluded()
    {
        // Category entries may be absolute rather than relative to a base_path. They are
        // still categories, so they are still not roots.
        var install = Dir("Comfy2");
        File.WriteAllText(Path.Combine(install, "main.py"), "# comfy");
        var absoluteLoras = Dir("Elsewhere", "MyLoras");

        File.WriteAllLines(Path.Combine(install, "extra_model_paths.yaml"),
        [
            "comfyui:",
            $"    loras: {absoluteLoras}",
        ]);

        ComfyUiPathDiscovery.EnumerateModelRoots(install).Should().NotContain(absoluteLoras);
        ComfyUiPathDiscovery.EnumerateModelSearchPaths(install).Should().Contain(absoluteLoras);
    }

    [Fact]
    public void EnumerateModelRoots_PortableLayout_IncludesTheSiblingModelsFolder()
    {
        var portableRoot = Dir("Portable");
        Dir("Portable", "ComfyUI");
        File.WriteAllText(Path.Combine(portableRoot, "ComfyUI", "main.py"), "# comfy");
        var siblingModels = Dir("Portable", "models");

        ComfyUiPathDiscovery.EnumerateModelRoots(portableRoot).Should().Contain(siblingModels);
    }

    [Fact]
    public void EnumerateModelRoots_BasePathThatDoesNotExist_IsIgnored()
    {
        var install = Dir("Comfy3");
        File.WriteAllText(Path.Combine(install, "main.py"), "# comfy");
        var missing = Path.Combine(_tempRoot.FullName, "not-created");

        File.WriteAllLines(Path.Combine(install, "extra_model_paths.yaml"),
        [
            "comfyui:",
            $"    base_path: {missing}",
        ]);

        ComfyUiPathDiscovery.EnumerateModelRoots(install).Should().NotContain(missing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnumerateModelRoots_BlankRoot_ReturnsEmpty(string? rootPath)
    {
        ComfyUiPathDiscovery.EnumerateModelRoots(rootPath!).Should().BeEmpty();
    }
}
