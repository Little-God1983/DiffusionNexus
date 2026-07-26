using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Proves the <see cref="IUiScheduler"/> seam on <see cref="SettingsViewModel"/>:
/// <c>OnExternalSettingsSaved</c> posts a collection reload onto the UI thread.
/// With <see cref="ImmediateUiScheduler"/> that post runs inline, so the reload is
/// observable synchronously — no Avalonia dispatcher required.
/// </summary>
public class SettingsViewModelSchedulerTests
{
    [Fact]
    public void WhenAnExternalSettingsSavedArrivesThenCollectionsReloadSynchronouslyThroughTheScheduler()
    {
        var appSettings = new AppSettings();
        appSettings.LoraSources.Add(new LoraSource
        {
            Id = 3,
            FolderPath = @"C:\Models\Lora",
            IsEnabled = true,
            Order = 0
        });

        var settingsService = new Mock<IAppSettingsService>();
        settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(appSettings);

        // Real in-process aggregator — it invokes handlers synchronously on the
        // caller's thread, so the only thing marshalling work off it is the seam.
        var aggregator = new DatasetEventAggregator();
        var scheduler = new ImmediateUiScheduler();

        var vm = new SettingsViewModel(
            settingsService.Object,
            new Mock<ISecureStorage>().Object,
            eventAggregator: aggregator,
            uiScheduler: scheduler);

        vm.LoraSources.Should().BeEmpty("no reload has been triggered yet");

        // An external component (e.g. the Installer Manager adding a gallery)
        // announces that settings were saved.
        aggregator.PublishSettingsSaved(new SettingsSavedEventArgs());

        // OnExternalSettingsSaved posted the reload through the scheduler; the
        // immediate scheduler ran it inline, so the reloaded source is already here.
        scheduler.PostCount.Should().Be(1);
        vm.LoraSources.Should().ContainSingle(s => s.FolderPath == @"C:\Models\Lora");
    }
}
