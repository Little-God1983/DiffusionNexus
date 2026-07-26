using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Covers the ⚠ "folder not found on disk" indicator shared by the three Settings
/// folder lists (LoRA Viewer sources, Generation Galleries, Base Model Folders).
/// Presence is NEVER probed in the row's property setter — that would put blocking
/// disk IO (seconds on an offline UNC share) on the UI thread per keystroke and on
/// the startup-gating settings load. Instead the parent VM scans all rows off-thread
/// via <see cref="SettingsViewModel.RefreshFolderPresenceAsync"/> (after load, on view
/// attach) and debounced after row edits.
/// </summary>
public sealed class SettingsFolderRowMissingTests : IDisposable
{
    private readonly string _existingDir = Directory.CreateTempSubdirectory("dn-folder-row-").FullName;
    private readonly string _missingDir = Path.Combine(Path.GetTempPath(), "dn-folder-row-missing-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_existingDir, recursive: true); } catch { }
    }

    private static SettingsViewModel CreateSut() => new(
        new Mock<IAppSettingsService>().Object,
        new Mock<ISecureStorage>().Object);

    // --- FolderRowPresence: the pure check --------------------------------------

    [Fact]
    public void Presence_FlagsMissing_WhenPathDoesNotExistOnDisk()
    {
        FolderRowPresence.IsMissing(_missingDir).Should().BeTrue();
    }

    [Fact]
    public void Presence_DoesNotFlag_WhenPathExists()
    {
        FolderRowPresence.IsMissing(_existingDir).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Presence_DoesNotFlag_EmptyPaths(string? emptyPath)
    {
        FolderRowPresence.IsMissing(emptyPath).Should().BeFalse();
    }

    // --- Row setters stay IO-free ------------------------------------------------

    [Fact]
    public void SettingAPath_DoesNotProbeTheDiskSynchronously()
    {
        // The badge appears only after an async presence scan — never from the
        // setter itself (per-keystroke disk IO on the UI thread).
        new LoraSourceViewModel { FolderPath = _missingDir }.IsMissing.Should().BeFalse();
        new ImageGalleryViewModel { FolderPath = _missingDir }.IsMissing.Should().BeFalse();
        new BaseModelFolderViewModel { FolderPath = _missingDir }.IsMissing.Should().BeFalse();
    }

    // --- The async scan ------------------------------------------------------------

    [Fact]
    public async Task RefreshFolderPresenceAsync_FlagsMissingRows_AcrossAllThreeLists()
    {
        var sut = CreateSut();
        var lora = new LoraSourceViewModel { FolderPath = _missingDir };
        var loraOk = new LoraSourceViewModel { FolderPath = _existingDir };
        var gallery = new ImageGalleryViewModel { FolderPath = _missingDir };
        var baseFolder = new BaseModelFolderViewModel { FolderPath = _missingDir };
        var baseEmpty = new BaseModelFolderViewModel();
        sut.LoraSources.Add(lora);
        sut.LoraSources.Add(loraOk);
        sut.ImageGallerySources.Add(gallery);
        sut.BaseModelFolders.Add(baseFolder);
        sut.BaseModelFolders.Add(baseEmpty);

        await sut.RefreshFolderPresenceAsync();

        lora.IsMissing.Should().BeTrue();
        loraOk.IsMissing.Should().BeFalse();
        gallery.IsMissing.Should().BeTrue();
        baseFolder.IsMissing.Should().BeTrue();
        baseEmpty.IsMissing.Should().BeFalse("a fresh row without a path must not warn");
    }

    [Fact]
    public async Task RefreshFolderPresenceAsync_ClearsTheBadge_WhenTheFolderCameBack()
    {
        var sut = CreateSut();
        var row = new BaseModelFolderViewModel { FolderPath = _existingDir, IsMissing = true };
        sut.BaseModelFolders.Add(row);

        await sut.RefreshFolderPresenceAsync();

        row.IsMissing.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshFolderPresenceAsync_SeesDiskChanges_SinceTheRowWasLoaded()
    {
        var vanishingDir = Directory.CreateTempSubdirectory("dn-folder-row-vanish-").FullName;
        var sut = CreateSut();
        var lora = new LoraSourceViewModel { FolderPath = vanishingDir };
        var gallery = new ImageGalleryViewModel { FolderPath = vanishingDir };
        var baseFolder = new BaseModelFolderViewModel { FolderPath = vanishingDir };
        sut.LoraSources.Add(lora);
        sut.ImageGallerySources.Add(gallery);
        sut.BaseModelFolders.Add(baseFolder);

        Directory.Delete(vanishingDir);
        await sut.RefreshFolderPresenceAsync();

        lora.IsMissing.Should().BeTrue();
        gallery.IsMissing.Should().BeTrue();
        baseFolder.IsMissing.Should().BeTrue();
    }

    // --- Debounced re-check after row edits ------------------------------------

    [Fact]
    public async Task EditingARowPath_RechecksPresence_AfterTheDebounce()
    {
        var sut = CreateSut();
        sut.PresenceRefreshDebounce = TimeSpan.FromMilliseconds(20);
        sut.AddBaseModelFolderCommand.Execute(null);
        var row = sut.BaseModelFolders[0];

        row.FolderPath = _missingDir;

        (await WaitForAsync(() => row.IsMissing)).Should().BeTrue(
            "the debounced scan should flag the missing folder shortly after the edit");
    }

    [Fact]
    public async Task CorrectingARowPath_ClearsTheBadge_OnTheNextScan()
    {
        var sut = CreateSut();
        var row = new LoraSourceViewModel { FolderPath = _missingDir, IsMissing = true };
        sut.LoraSources.Add(row);

        row.FolderPath = _existingDir;
        await sut.RefreshFolderPresenceAsync();

        row.IsMissing.Should().BeFalse();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }
}
