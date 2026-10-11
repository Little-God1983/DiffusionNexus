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
        _settings.Setup(s => s.SetCatalogChannelIfSettingsExistAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback((string c, CancellationToken _) => _stored = _stored with { Channel = c }).ReturnsAsync(true);
        _settings.Setup(s => s.SetApplyCatalogUpdatesAutomaticallyAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                 .Callback((bool on, CancellationToken _) => _stored = _stored with { ApplyAutomatically = on }).Returns(Task.CompletedTask);
    }

    private string? _installerFile;

    private CatalogPreferenceStore Store()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_settings.Object);
        return new CatalogPreferenceStore(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _installer.Object, _log, _installerFile);
    }

    // PR #619 round 3: the SDK's JSON repository reads a locked or corrupt user_settings.json as empty instead of
    // throwing. A file that exists but yields no user is unknown, not "nothing saved": nothing is stored.
    [Fact]
    public async Task An_installer_file_that_yields_no_user_is_unknown_and_stores_nothing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            _installerFile = Path.Combine(dir.FullName, "user_settings.json");
            File.WriteAllText(_installerFile, "[ locked or corrupt ]");
            InstallerSaved(null);

            (await Store().LoadAsync()).Channel.Should().BeNull();
            _settings.Verify(s => s.SetCatalogChannelIfSettingsExistAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { dir.Delete(recursive: true); }
    }

    // PR #619 round 5: a valid file without this user's row (a renamed account) is "nothing saved", not unreadable.
    [Fact]
    public async Task A_readable_installer_file_without_this_user_starts_on_Stable_and_stores_it()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            _installerFile = Path.Combine(dir.FullName, "user_settings.json");
            File.WriteAllText(_installerFile, "[]");
            InstallerSaved(null);

            (await Store().LoadAsync()).Channel.Should().Be("Stable");
            _settings.Verify(s => s.SetCatalogChannelIfSettingsExistAsync("Stable", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Without_an_installer_file_the_app_starts_on_Stable_and_stores_it()
    {
        _installerFile = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json");
        InstallerSaved(null);

        (await Store().LoadAsync()).Channel.Should().Be("Stable");
        _settings.Verify(s => s.SetCatalogChannelIfSettingsExistAsync("Stable", It.IsAny<CancellationToken>()), Times.Once);
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
        _settings.Verify(s => s.SetCatalogChannelIfSettingsExistAsync("Preview", It.IsAny<CancellationToken>()), Times.Once);
        _log.GetEntries().Should().Contain(e => e.Source == "Catalog" && e.Message.Contains("installer's Preview channel"));

        InstallerSaved("Stable");   // the installer changes its mind later
        (await Store().LoadAsync()).Channel.Should().Be("Preview", "the copy happens once; the apps choose independently after that");
        _settings.Verify(s => s.SetCatalogChannelIfSettingsExistAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nonsense")]
    public async Task Nothing_usable_saved_by_the_installer_starts_on_Stable(string? installerChannel)
    {
        InstallerSaved(installerChannel);

        (await Store().LoadAsync()).Channel.Should().Be("Stable");
        _settings.Verify(s => s.SetCatalogChannelIfSettingsExistAsync("Stable", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_unreadable_installer_file_stores_nothing_and_is_tried_again_next_start()
    {
        _installer.Setup(r => r.GetByUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("locked"));

        // PR #619 round 1: storing the Stable fallback made a Preview user Stable for good.
        (await Store().LoadAsync()).Channel.Should().BeNull("nothing is known; the coordinator follows Stable for this run only");
        _settings.Verify(s => s.SetCatalogChannelIfSettingsExistAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _installer.Verify(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
        _installer.Verify(r => r.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Review M5: the first save failed (database locked) and the run followed Stable for a Preview user.
    [Fact]
    public async Task A_failed_first_save_still_follows_the_installers_channel()
    {
        InstallerSaved("Preview");
        _settings.Setup(s => s.SetCatalogChannelIfSettingsExistAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("database locked"));

        (await Store().LoadAsync()).Channel.Should().Be("Preview");
        _log.GetEntries().Should().Contain(e => e.Source == "Catalog" && e.Message.Contains("could not be saved"));
    }

    // PR #619 round 2: on a fresh install the copy must not create the settings row (the UI does, at the same time).
    [Fact]
    public async Task Without_a_settings_row_the_copy_is_followed_for_this_run_and_saved_next_start()
    {
        InstallerSaved("Preview");
        _settings.Setup(s => s.SetCatalogChannelIfSettingsExistAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        (await Store().LoadAsync()).Channel.Should().Be("Preview");
        _settings.Verify(s => s.SetCatalogChannelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
