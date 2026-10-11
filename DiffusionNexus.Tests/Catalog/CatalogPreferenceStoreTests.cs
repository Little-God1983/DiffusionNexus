using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>#618: the app's own catalog preferences; the installer's channel is copied once, read-only.</summary>
public sealed class CatalogPreferenceStoreTests
{
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IUserSettingsRepository> _installer = new();
    private readonly UnifiedLogger _log = new();
    private CatalogUpdateSettings _stored = new(null, false);

    public CatalogPreferenceStoreTests()
    {
        _settings.Setup(s => s.GetCatalogUpdateSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _stored);
        _settings.Setup(s => s.SetCatalogChannelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback((string c, CancellationToken _) => _stored = _stored with { Channel = c }).Returns(Task.CompletedTask);
        _settings.Setup(s => s.SetApplyCatalogUpdatesAutomaticallyAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                 .Callback((bool on, CancellationToken _) => _stored = _stored with { ApplyAutomatically = on }).Returns(Task.CompletedTask);
    }

    private CatalogPreferenceStore Store()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_settings.Object);
        return new CatalogPreferenceStore(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _installer.Object, _log);
    }

    private void InstallerSaved(string? channel) =>
        _installer.Setup(r => r.GetByUserNameAsync(Environment.UserName, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(channel is null ? null : new UserSettings { UserName = Environment.UserName, CatalogChannel = channel });

    [Fact]
    public async Task The_first_start_copies_the_installers_channel_once_and_saves_it()
    {
        InstallerSaved("Preview");

        var first = await Store().LoadAsync();

        first.Channel.Should().Be("Preview");
        _settings.Verify(s => s.SetCatalogChannelAsync("Preview", It.IsAny<CancellationToken>()), Times.Once);
        _log.GetEntries().Should().Contain(e => e.Source == "Catalog" && e.Message.Contains("installer's Preview channel"));

        InstallerSaved("Stable");   // the installer changes its mind later
        (await Store().LoadAsync()).Channel.Should().Be("Preview", "the copy happens once; the apps choose independently after that");
        _settings.Verify(s => s.SetCatalogChannelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nonsense")]
    public async Task Nothing_usable_saved_by_the_installer_starts_on_Stable(string? installerChannel)
    {
        InstallerSaved(installerChannel);

        (await Store().LoadAsync()).Channel.Should().Be("Stable");
        _settings.Verify(s => s.SetCatalogChannelAsync("Stable", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_unreadable_installer_file_starts_on_Stable_and_never_writes_it()
    {
        _installer.Setup(r => r.GetByUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("locked"));

        (await Store().LoadAsync()).Channel.Should().Be("Stable");
        _installer.Verify(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
        _installer.Verify(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_chosen_channel_never_reads_the_installers_file()
    {
        _stored = new CatalogUpdateSettings("Stable", true);

        var loaded = await Store().LoadAsync();

        loaded.Should().Be(new CatalogPreferences("Stable", true));
        _installer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Saves_go_through_their_own_setters()
    {
        var store = Store();

        await store.SaveChannelAsync(CatalogChannel.Preview);
        await store.SaveApplyAutomaticallyAsync(true);

        _stored.Should().Be(new CatalogUpdateSettings("Preview", true));
    }
}
