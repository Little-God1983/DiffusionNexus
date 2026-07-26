using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ComfyUiPathDiscovery.EnumerateModelSearchPaths"/>.
/// Builds throwaway ComfyUI directory trees (manual and Windows-portable layouts)
/// under a temp folder and asserts which roots are discovered.
/// </summary>
public class ComfyUiPathDiscoveryTests : IDisposable
{
    private readonly DirectoryInfo _tempRoot;

    public ComfyUiPathDiscoveryTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory();
    }

    public void Dispose()
    {
        try { _tempRoot.Delete(recursive: true); }
        catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private string Dir(params string[] segments)
    {
        var path = Path.Combine(new[] { _tempRoot.FullName }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string Touch(params string[] segments)
    {
        var path = Path.Combine(new[] { _tempRoot.FullName }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static void WriteYaml(string repositoryPath, params string[] lines) =>
        File.WriteAllLines(Path.Combine(repositoryPath, "extra_model_paths.yaml"), lines);

    #region Guard clauses

    [Fact]
    public void WhenRootPathIsNullThenNoPathsAreReturned()
    {
        ComfyUiPathDiscovery.EnumerateModelSearchPaths(null!).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenRootPathIsBlankThenNoPathsAreReturned(string rootPath)
    {
        ComfyUiPathDiscovery.EnumerateModelSearchPaths(rootPath).Should().BeEmpty();
    }

    [Fact]
    public void WhenRootPathDoesNotExistThenNoPathsAreReturned()
    {
        var missing = Path.Combine(_tempRoot.FullName, "does-not-exist");

        ComfyUiPathDiscovery.EnumerateModelSearchPaths(missing).Should().BeEmpty();
    }

    #endregion

    #region Manual installs

    [Fact]
    public void WhenManualInstallHasModelsFolderThenItIsTheOnlySearchPath()
    {
        var root = Dir("manual");
        var models = Dir("manual", "models");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().ContainSingle().Which.Should().Be(models);
    }

    [Fact]
    public void WhenManualInstallHasNoModelsFolderThenNoPathsAreReturned()
    {
        var root = Dir("bare");

        ComfyUiPathDiscovery.EnumerateModelSearchPaths(root).Should().BeEmpty();
    }

    [Fact]
    public void WhenRootHasMainPyDirectlyThenItIsTreatedAsAManualInstall()
    {
        var root = Dir("manual");
        Touch("manual", "main.py");
        var models = Dir("manual", "models");
        // A sibling "models" next to the root must NOT be picked up for manual installs.
        Dir("models");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().ContainSingle().Which.Should().Be(models);
    }

    #endregion

    #region Portable installs

    [Fact]
    public void WhenPortableInstallThenTheInnerRepositoryModelsFolderIsUsed()
    {
        var root = Dir("portable");
        Touch("portable", "ComfyUI", "main.py");
        var innerModels = Dir("portable", "ComfyUI", "models");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().ContainSingle().Which.Should().Be(innerModels);
    }

    [Fact]
    public void WhenPortableInstallHasSiblingModelsFolderThenBothRootsAreReturned()
    {
        var root = Dir("portable");
        Touch("portable", "ComfyUI", "main.py");
        var innerModels = Dir("portable", "ComfyUI", "models");
        var siblingModels = Dir("portable", "models");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().BeEquivalentTo(new[] { innerModels, siblingModels });
    }

    [Fact]
    public void WhenPortableInstallHasNoSiblingModelsFolderThenOnlyTheInnerRootIsReturned()
    {
        var root = Dir("portable");
        Touch("portable", "ComfyUI", "main.py");
        var innerModels = Dir("portable", "ComfyUI", "models");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().ContainSingle().Which.Should().Be(innerModels);
    }

    [Fact]
    public void WhenPortableInstallHasOnlySiblingModelsFolderThenThatIsReturned()
    {
        var root = Dir("portable");
        Touch("portable", "ComfyUI", "main.py");
        var siblingModels = Dir("portable", "models");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().ContainSingle().Which.Should().Be(siblingModels);
    }

    [Fact]
    public void WhenComfyUiSubfolderHasNoMainPyThenTheRootIsTreatedAsManual()
    {
        var root = Dir("ambiguous");
        Dir("ambiguous", "ComfyUI", "models"); // subfolder exists but no main.py
        var rootModels = Dir("ambiguous", "models");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().ContainSingle().Which.Should().Be(rootModels);
    }

    #endregion

    #region extra_model_paths.yaml merge

    [Fact]
    public void WhenYamlDeclaresBasePathThenItIsMergedWithTheRepositoryModelsFolder()
    {
        var root = Dir("manual");
        var models = Dir("manual", "models");
        var extraBase = Dir("extra");
        WriteYaml(root, "comfyui:", $"    base_path: {extraBase}");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().BeEquivalentTo(new[] { models, extraBase });
    }

    [Fact]
    public void WhenYamlHasRelativeEntriesThenTheyResolveAgainstTheBasePath()
    {
        var root = Dir("manual");
        var models = Dir("manual", "models");
        var extraBase = Dir("extra");
        var checkpoints = Dir("extra", "checkpoints");
        WriteYaml(root,
            "comfyui:",
            $"    base_path: {extraBase}",
            "    checkpoints: checkpoints");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().BeEquivalentTo(new[] { models, extraBase, checkpoints });
    }

    [Fact]
    public void WhenYamlHasAbsoluteEntriesThenTheyAreUsedDirectly()
    {
        var root = Dir("manual");
        var models = Dir("manual", "models");
        var loras = Dir("elsewhere", "loras");
        WriteYaml(root, "comfyui:", $"    loras: {loras}");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().BeEquivalentTo(new[] { models, loras });
    }

    [Fact]
    public void WhenYamlPointsAtMissingDirectoriesThenTheyAreIgnored()
    {
        var root = Dir("manual");
        var models = Dir("manual", "models");
        var ghost = Path.Combine(_tempRoot.FullName, "ghost");
        WriteYaml(root, "comfyui:", $"    loras: {ghost}");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().ContainSingle().Which.Should().Be(models);
    }

    [Fact]
    public void WhenYamlHasCommentsAndBlankLinesThenTheyAreSkipped()
    {
        var root = Dir("manual");
        var models = Dir("manual", "models");
        var extraBase = Dir("extra");
        WriteYaml(root,
            "# a comment",
            "",
            "   ",
            "comfyui:",
            $"    base_path: {extraBase}");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().BeEquivalentTo(new[] { models, extraBase });
    }

    [Fact]
    public void WhenYamlValueIsQuotedThenTheQuotesAreStripped()
    {
        var root = Dir("manual");
        var models = Dir("manual", "models");
        var extraBase = Dir("extra");
        WriteYaml(root, "comfyui:", $"    base_path: \"{extraBase}\"");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().BeEquivalentTo(new[] { models, extraBase });
    }

    [Fact]
    public void WhenNoYamlExistsThenOnlyTheRepositoryModelsFolderIsReturned()
    {
        var root = Dir("manual");
        var models = Dir("manual", "models");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().ContainSingle().Which.Should().Be(models);
    }

    [Fact]
    public void WhenPortableInstallHasYamlThenItIsReadFromTheInnerRepository()
    {
        var root = Dir("portable");
        Touch("portable", "ComfyUI", "main.py");
        var innerModels = Dir("portable", "ComfyUI", "models");
        var extraBase = Dir("extra");
        WriteYaml(Path.Combine(root, "ComfyUI"), "comfyui:", $"    base_path: {extraBase}");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().BeEquivalentTo(new[] { innerModels, extraBase });
    }

    #endregion

    #region Case-insensitive de-duplication

    [Fact]
    public void WhenYamlRepeatsTheRepositoryModelsFolderInDifferentCasingThenItIsNotDuplicated()
    {
        var root = Dir("manual");
        var models = Dir("manual", "models");
        WriteYaml(root, "comfyui:", $"    checkpoints: {models.ToUpperInvariant()}");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().ContainSingle();
    }

    [Fact]
    public void WhenYamlListsTheSamePathTwiceThenItAppearsOnce()
    {
        var root = Dir("manual");
        Dir("manual", "models");
        var extra = Dir("extra");
        WriteYaml(root,
            "comfyui:",
            $"    checkpoints: {extra}",
            $"    loras: {extra}");

        var paths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(root);

        paths.Should().HaveCount(2);
    }

    #endregion
}
