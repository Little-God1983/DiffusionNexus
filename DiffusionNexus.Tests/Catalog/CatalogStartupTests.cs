using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>
/// The first catalog load at startup: channel resolution and the Unified Console report. Runs off the
/// UI thread because <see cref="ICatalog.Source"/> blocks while the embedded seed unpacks.
/// </summary>
public class CatalogStartupTests
{
    private readonly Mock<ICatalog> _catalog = new();
    private readonly Mock<IUserSettingsRepository> _settings = new();
    private readonly CatalogOptions _options = new();
    private readonly ActivityLogService _log = new();

    public CatalogStartupTests()
    {
        _catalog.SetupGet(c => c.Source).Returns(new CatalogSource(CatalogSourceKind.Installed, @"C:\catalog", SeededFromEmbedded: true));
        _catalog.SetupGet(c => c.State).Returns(new LocalCatalogState
        {
            Workloads = new SectionState(5, "abc", DateTimeOffset.UnixEpoch),
            Workflows = new SectionState(4, "abc", DateTimeOffset.UnixEpoch)
        });
        _catalog.SetupGet(c => c.Diagnostics).Returns([]);
        SavedChannel(null);
    }

    private void SavedChannel(string? channel)
        => _settings.Setup(s => s.GetByUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel is null ? null : new UserSettings { UserName = Environment.UserName, CatalogChannel = channel });

    private Task<CatalogChannel> Run(string? env = null)
        => CatalogStartup.InitializeAsync(_catalog.Object, _options, _settings.Object, env, _log);

    [Fact]
    public async Task Follows_the_channel_the_installer_saved()
    {
        SavedChannel("Preview");

        var channel = await Run();

        channel.Should().Be(CatalogChannel.Preview);
        _options.Channel.Should().Be(CatalogChannel.Preview);
    }

    [Fact]
    public async Task Environment_channel_wins_over_the_saved_one()
    {
        SavedChannel("Preview");

        (await Run(env: "stable")).Should().Be(CatalogChannel.Stable);
        _options.Channel.Should().Be(CatalogChannel.Stable);
    }

    [Fact]
    public async Task Never_writes_the_user_settings()
    {
        SavedChannel(null);

        await Run();

        _settings.Verify(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);
        _settings.Verify(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Logs_where_the_catalog_came_from_and_its_versions()
    {
        await Run();

        var messages = _log.GetEntries().Select(e => e.Message).ToList();
        messages.Should().Contain(@"Catalog: Installed at C:\catalog (seeded from embedded)");
        messages.Should().Contain("Catalog version: workloads v5, workflows v4, channel Stable");
        _log.GetEntries().Should().OnlyContain(e => e.Source == CatalogStartup.LogSource);
    }

    [Fact]
    public async Task Logs_one_entry_per_diagnostic_with_its_code_and_severity()
    {
        _catalog.SetupGet(c => c.Diagnostics).Returns(
        [
            new CatalogDiagnostic(CatalogDiagnosticSeverity.Error, CatalogDiagnosticCodes.UnsupportedSchema, "schema 2 is newer"),
            new CatalogDiagnostic(CatalogDiagnosticSeverity.Warning, CatalogDiagnosticCodes.OverrideMissing, "override gone")
        ]);

        await Run();

        _log.GetEntries().Should().ContainSingle(e =>
            e.Severity == ActivitySeverity.Error && e.Message.Contains("CAT001") && e.Message.Contains("schema 2 is newer"));
        _log.GetEntries().Should().ContainSingle(e =>
            e.Severity == ActivitySeverity.Warning && e.Message.Contains("CATL001") && e.Message.Contains("override gone"));
    }

    [Fact]
    public async Task A_failing_catalog_is_logged_and_startup_continues()
    {
        _catalog.SetupGet(c => c.Source).Throws(new IOException("disk on fire"));

        var act = () => Run();

        await act.Should().NotThrowAsync();
        _log.GetEntries().Should().Contain(e => e.Severity == ActivitySeverity.Error);
    }

    [Fact]
    public async Task A_failing_settings_read_still_resolves_a_channel()
    {
        _settings.Setup(s => s.GetByUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("locked"));

        (await Run(env: "preview")).Should().Be(CatalogChannel.Preview);
        _log.GetEntries().Should().Contain(e => e.Severity == ActivitySeverity.Warning);
    }
}
