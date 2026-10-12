using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// #612: after the Engine installs, the app asks once whether the Server setting should move to it,
/// and the Engine tile greys out Remove and Delete from Disk.
/// </summary>
public class EngineServerOfferTests
{
    private const int KeepMyComfyUi = 0;
    private const int UseTheEngine = 1;

    private readonly Mock<IDialogService> _dialog = new();
    private readonly Mock<IDatasetEventAggregator> _events = new();
    private readonly Mock<IUnifiedLogger> _logger = new();
    private readonly Mock<IAppSettingsRepository> _appSettings = new();
    private AppSettings? _stored;

    private InstallerManagerViewModel Create(ComfyUiServerMode? storedMode, bool installSucceeds = true)
    {
        _stored = storedMode is null ? null : new AppSettings { Id = 1, ComfyUiServerMode = storedMode.Value };
        _appSettings.Setup(r => r.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _stored);
        _dialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(@"C:\Engine\New");

        var installer = new Mock<IManagedEngineInstaller>();
        installer.Setup(i => i.InstallBaseEngineAsync(
                It.IsAny<EngineInstallRequest>(), It.IsAny<IProgress<InstallLogEntry>>(),
                It.IsAny<IProgress<InstallationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(installSucceeds
                ? new EngineInstallOutcome(true, false, "done", @"C:\Engine\New")
                : new EngineInstallOutcome(false, false, "pip failed", null));

        var vm = EngineTestHarness.CreateInstallerManagerViewModel(
            [], engineInstaller: installer.Object, onPackageAdded: _ => { }, dialogMock: _dialog,
            eventAggregatorMock: _events, loggerMock: _logger, appSettingsMock: _appSettings);
        vm.IsEngineTileVisible = true;
        return vm;
    }

    private void Answer(int choice) =>
        _dialog.Setup(d => d.ShowOptionsAsync(InstallerManagerViewModel.EngineOfferTitle, It.IsAny<string>(),
                It.IsAny<string[]>()))
            .ReturnsAsync(choice);

    private async Task InstallAsync(InstallerManagerViewModel vm)
    {
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        await vm.InstallEngineAsync();
    }

    private void VerifyAsked(Times times) =>
        _dialog.Verify(d => d.ShowOptionsAsync(InstallerManagerViewModel.EngineOfferTitle, It.IsAny<string>(),
            It.IsAny<string[]>()), times);

    [Fact]
    public async Task UseTheEngine_SavesTheServer_AndLetsOpenPanelsRecheck()
    {
        var vm = Create(ComfyUiServerMode.CustomUrl);
        Answer(UseTheEngine);

        await InstallAsync(vm);

        _stored!.ComfyUiServerMode.Should().Be(ComfyUiServerMode.Engine);
        _events.Verify(e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()), Times.Once,
            "Settings and the editor's readiness lines re-check on SettingsSaved, as after a save in Settings");
        _logger.Verify(l => l.Info(LogCategory.Installation, "Diffusion Nexus Engine",
            It.Is<string>(m => m.StartsWith("Server set to the Diffusion Nexus Engine")
                               && m.Contains("Outpaint (also with Vision)") && m.Contains("Batch Upscale")), null), Times.Once);
    }

    [Fact]
    public async Task TheQuestion_NamesEveryFeatureThatMoves_AndOffersBothAnswers()
    {
        var vm = Create(ComfyUiServerMode.CustomUrl);
        string? message = null;
        string[]? options = null;
        _dialog.Setup(d => d.ShowOptionsAsync(InstallerManagerViewModel.EngineOfferTitle, It.IsAny<string>(),
                It.IsAny<string[]>()))
            .Callback<string, string, string[]>((_, m, o) => { message = m; options = o; })
            .ReturnsAsync(KeepMyComfyUi);

        await InstallAsync(vm);

        message.Should().Contain("Inpaint").And.Contain("Outpaint (also with Vision)").And.Contain("Batch Upscale")
            .And.Contain("Settings → ComfyUI Server");
        message.Should().Contain("Diffusion Nexus Engine → Features",
            "the base install carries no features; the user must know they are installed separately");
        options.Should().Equal("Keep my ComfyUI", "Use the Engine");
    }

    [Theory]
    [InlineData(KeepMyComfyUi)]
    [InlineData(-1)] // closed without an answer
    public async Task KeepMyComfyUi_OrClosing_ChangesNothing(int choice)
    {
        var vm = Create(ComfyUiServerMode.CustomUrl);
        Answer(choice);

        await InstallAsync(vm);

        _stored!.ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl);
        _events.Verify(e => e.PublishSettingsSaved(It.IsAny<SettingsSavedEventArgs>()), Times.Never);
        _logger.Verify(l => l.Info(LogCategory.Installation, "Diffusion Nexus Engine",
            It.Is<string>(m => m.StartsWith("Kept your own ComfyUI")), null), Times.Once);
    }

    [Fact]
    public async Task ServerAlreadyTheEngine_DoesNotAsk()
    {
        var vm = Create(ComfyUiServerMode.Engine);

        await InstallAsync(vm);

        VerifyAsked(Times.Never());
    }

    [Fact]
    public async Task NoSettingsRowYet_DoesNotAsk_ItReadsAsTheEngine()
    {
        var vm = Create(storedMode: null);

        await InstallAsync(vm);

        VerifyAsked(Times.Never());
    }

    [Fact]
    public async Task FailedInstall_DoesNotAsk()
    {
        var vm = Create(ComfyUiServerMode.CustomUrl, installSucceeds: false);

        await InstallAsync(vm);

        VerifyAsked(Times.Never());
    }

    [Fact]
    public async Task AskedOnlyOnceTheTileShowsTheFinishedInstall()
    {
        var vm = Create(ComfyUiServerMode.CustomUrl);
        await vm.LoadInstallationsCommand.ExecuteAsync(null);
        var card = vm.InstallerCards.Single(c => c.IsEngine);
        bool? installingWhenAsked = null;
        _dialog.Setup(d => d.ShowOptionsAsync(InstallerManagerViewModel.EngineOfferTitle, It.IsAny<string>(),
                It.IsAny<string[]>()))
            .Callback(() => installingWhenAsked = card.IsEngineInstalling)
            .ReturnsAsync(KeepMyComfyUi);

        await vm.InstallEngineAsync();

        installingWhenAsked.Should().BeFalse();
    }

    [Fact]
    public async Task SaveThatFails_IsAWarning_NotAFailedInstall()
    {
        var vm = Create(ComfyUiServerMode.CustomUrl);
        Answer(UseTheEngine);
        _appSettings.Setup(r => r.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database locked"));

        await InstallAsync(vm);

        vm.InstallerCards.Single(c => c.IsEngine).IsEngineInstalled.Should().BeTrue();
        _dialog.Verify(d => d.ShowMessageAsync("Engine install failed", It.IsAny<string>()), Times.Never);
        _logger.Verify(l => l.Warn(LogCategory.Installation, "Diffusion Nexus Engine",
            It.Is<string>(m => m.Contains("offer the Engine as the Server")), null), Times.Once);
    }

    [Fact]
    public void EngineTile_GreysOutRemoveAndDeleteFromDisk_AndSaysWhy()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(null);

        card.RemoveCommand.CanExecute(null).Should().BeFalse();
        card.DeleteFromDiskCommand.CanExecute(null).Should().BeFalse();
        card.RemoveTooltip.Should().Contain("The app manages the Diffusion Nexus Engine");
        card.DeleteFromDiskTooltip.Should().Contain("must be stopped first");
    }

    // Review round 2: the tooltip claimed files on disk on a tile with no Engine installed.
    [Fact]
    public void EngineTile_NotInstalled_TooltipsClaimNoInstall()
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(null);

        card.RemoveTooltip.Should().NotContain("files on disk");
        card.DeleteFromDiskTooltip.Should().NotContain("left broken");
    }

    // Review round 2: the Core card shares the ⋮ menu, and its Remove and Delete were wired to nothing.
    [Fact]
    public void CoreTile_GreysOutRemoveAndDeleteFromDisk_AndSaysWhy()
    {
        var card = InstallerPackageCardViewModel.CreateCoreCard();

        card.RemoveCommand.CanExecute(null).Should().BeFalse();
        card.DeleteFromDiskCommand.CanExecute(null).Should().BeFalse();
        card.RemoveTooltip.Should().Contain("built into the app");
        card.DeleteFromDiskTooltip.Should().Contain("built into the app");
    }

    [Fact]
    public void OrdinaryInstallation_KeepsRemoveAndDeleteFromDisk()
    {
        var card = new InstallerPackageCardViewModel(new InstallerPackage
        {
            Id = 3, Name = "My ComfyUI", InstallationPath = @"C:\ComfyUI", ExecutablePath = null,
            Type = InstallerType.ComfyUI
        });

        card.RemoveCommand.CanExecute(null).Should().BeTrue();
        card.DeleteFromDiskCommand.CanExecute(null).Should().BeTrue();
        card.RemoveTooltip.Should().BeNull();
        card.DeleteFromDiskTooltip.Should().BeNull();
    }
}
