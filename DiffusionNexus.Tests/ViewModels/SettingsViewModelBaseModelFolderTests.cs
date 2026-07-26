using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Covers the "Base Model Folders" section of the Settings page: load mapping, the
/// exclusive ⭐ default toggle, add/remove commands, and the save snapshot
/// (including the pass-through of the installer-package link).
/// </summary>
public sealed class SettingsViewModelBaseModelFolderTests
{
    private readonly Mock<IAppSettingsService> _settingsService = new();
    private AppSettings _stored = new() { Id = 1 };

    private SettingsViewModel CreateSut()
    {
        _settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _stored);

        return new SettingsViewModel(
            _settingsService.Object,
            new Mock<ISecureStorage>().Object);
    }

    [Fact]
    public async Task Load_MapsBaseModelFolderRows()
    {
        _stored = new AppSettings
        {
            Id = 1,
            BaseModelFolders =
            [
                new BaseModelFolder { Id = 3, FolderPath = @"D:\ModelsA", IsEnabled = true, IsDefault = true, Order = 0, InstallerPackageId = 9 },
                new BaseModelFolder { Id = 4, FolderPath = @"E:\ModelsB", IsEnabled = false, Order = 1 },
            ],
        };

        var sut = CreateSut();
        await sut.LoadCommand.ExecuteAsync(null);

        sut.BaseModelFolders.Should().HaveCount(2);
        sut.BaseModelFolders[0].FolderPath.Should().Be(@"D:\ModelsA");
        sut.BaseModelFolders[0].IsDefault.Should().BeTrue();
        sut.BaseModelFolders[0].InstallerPackageId.Should().Be(9);
        sut.BaseModelFolders[1].IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SettingDefaultOnOneRow_ClearsTheOthers_AndFlagsChanges()
    {
        _stored = new AppSettings
        {
            Id = 1,
            BaseModelFolders =
            [
                new BaseModelFolder { Id = 1, FolderPath = @"D:\A", IsDefault = true, Order = 0 },
                new BaseModelFolder { Id = 2, FolderPath = @"D:\B", Order = 1 },
            ],
        };

        var sut = CreateSut();
        await sut.LoadCommand.ExecuteAsync(null);
        sut.HasChanges = false;

        sut.BaseModelFolders[1].IsDefault = true;

        sut.BaseModelFolders[0].IsDefault.Should().BeFalse("only one folder can be the default");
        sut.BaseModelFolders[1].IsDefault.Should().BeTrue();
        sut.HasChanges.Should().BeTrue();
    }

    [Fact]
    public async Task AddAndRemoveCommands_MutateTheCollection_AndFlagChanges()
    {
        var sut = CreateSut();
        await sut.LoadCommand.ExecuteAsync(null);
        sut.HasChanges = false;

        sut.AddBaseModelFolderCommand.Execute(null);
        sut.BaseModelFolders.Should().ContainSingle();
        sut.HasChanges.Should().BeTrue();

        sut.RemoveBaseModelFolderCommand.Execute(sut.BaseModelFolders[0]);
        sut.BaseModelFolders.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_SnapshotsRows_IncludingThePackageLink()
    {
        _stored = new AppSettings
        {
            Id = 1,
            // Disable backups so SaveAsync's backup validation doesn't veto the save.
            BackupDatabaseEnabled = false,
            BackupDatasetImagesEnabled = false,
            BaseModelFolders =
            [
                new BaseModelFolder { Id = 7, FolderPath = @"D:\Auto", IsEnabled = true, Order = 0, InstallerPackageId = 42 },
            ],
        };

        AppSettings? saved = null;
        _settingsService
            .Setup(s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppSettings, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.LoadCommand.ExecuteAsync(null);
        sut.BaseModelFolders[0].IsDefault = true;

        await sut.SaveCommand.ExecuteAsync(null);

        saved.Should().NotBeNull();
        var folder = saved!.BaseModelFolders.Should().ContainSingle().Subject;
        folder.Id.Should().Be(7);
        folder.FolderPath.Should().Be(@"D:\Auto");
        folder.IsDefault.Should().BeTrue();
        folder.InstallerPackageId.Should().Be(42, "the auto-registration link must survive a settings save");
    }
}
