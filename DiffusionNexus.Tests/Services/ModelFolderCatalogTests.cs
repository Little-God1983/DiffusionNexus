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
