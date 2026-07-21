using DiffusionNexus.Civitai;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Proves the #438 constructor-injection contract on <see cref="ModelDetailViewModel"/>:
/// the VM is fully constructible in a unit test with mocks/fakes (it no longer reaches
/// into the <c>App.Services</c> static locator), and the clipboard copy path routes
/// through the injected <see cref="IClipboardService"/> seam instead of a live Avalonia
/// <c>TopLevel</c>. No global Avalonia platform init is required (which would deadlock
/// the suite).
/// </summary>
public class ModelDetailViewModelTests
{
    /// <summary>Records the text handed to the clipboard seam.</summary>
    private sealed class RecordingClipboard : IClipboardService
    {
        public List<string> Copied { get; } = [];

        public Task SetTextAsync(string text)
        {
            Copied.Add(text);
            return Task.CompletedTask;
        }
    }

    private static ModelDetailViewModel CreateVm(
        IClipboardService? clipboard = null,
        IUiScheduler? scheduler = null)
        => new(
            civitaiClient: new Mock<ICivitaiClient>().Object,
            settingsService: new Mock<IAppSettingsService>().Object,
            secureStorage: new Mock<ISecureStorage>().Object,
            logger: new Mock<IUnifiedLogger>().Object,
            baseModelCatalog: null,
            scopeFactory: new Mock<IServiceScopeFactory>().Object,
            dialogService: new Mock<IDialogService>().Object,
            downloadService: null,
            downloadCoordinator: new Mock<IDownloadCoordinator>().Object,
            taskTracker: new Mock<ITaskTracker>().Object,
            activityLog: new Mock<IActivityLogService>().Object,
            clipboard: clipboard,
            uiScheduler: scheduler ?? new Helpers.ImmediateUiScheduler());

    [Fact]
    public void ConstructorWithMocksDoesNotThrowAndNeedsNoLocator()
    {
        var act = () => CreateVm();
        act.Should().NotThrow("the VM must be constructible with injected mocks, not App.Services");
    }

    [Fact]
    public async Task CopyTriggerWordsRoutesThroughTheInjectedClipboard()
    {
        var clipboard = new RecordingClipboard();
        var vm = CreateVm(clipboard);
        vm.TriggerWordsDisplay = "40fy, 3d style, fortnite";

        await vm.CopyTriggerWordsCommand.ExecuteAsync(null);

        clipboard.Copied.Should().ContainSingle().Which.Should().Be("40fy, 3d style, fortnite");
    }

    [Fact]
    public async Task CopyTriggerWordsWithNoTriggerWordsDoesNotTouchTheClipboard()
    {
        var clipboard = new RecordingClipboard();
        var vm = CreateVm(clipboard);
        vm.TriggerWordsDisplay = "   ";

        await vm.CopyTriggerWordsCommand.ExecuteAsync(null);

        clipboard.Copied.Should().BeEmpty();
    }
}
