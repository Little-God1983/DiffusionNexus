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
/// The background catalog startup: channel resolution, the Unified Console report, and the update
/// check for the resolved channel. Runs off the UI thread because <see cref="ICatalog.Source"/>
/// blocks while the embedded seed unpacks.
/// </summary>
public class CatalogStartupTests
{
    private readonly Mock<ICatalog> _catalog = new();
    private readonly Mock<IUserSettingsRepository> _settings = new();
    private readonly Mock<ICatalogUpdateService> _updates = new();
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
        _updates.Setup(u => u.CheckAsync(It.IsAny<CatalogChannel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CatalogChannel c, CancellationToken _) =>
                new CatalogUpdateCheck(CatalogUpdateOutcome.UpToDate, c, null, new LocalCatalogState(), [], [], null));
        SavedChannel(null);
    }

    private void SavedChannel(string? channel)
        => _settings.Setup(s => s.GetByUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel is null ? null : new UserSettings { UserName = Environment.UserName, CatalogChannel = channel });

    private Task<(CatalogChannel Channel, CatalogChannelSource Source)> Resolve(string? env = null)
        => CatalogStartup.ResolveChannelAsync(_settings.Object, env, _log);

    private void Report(CatalogChannel channel = CatalogChannel.Stable, CatalogChannelSource source = CatalogChannelSource.Default)
        => CatalogStartup.Report(_catalog.Object, channel, source, _log);

    private Task Run(string? env = null)
        => CatalogStartup.RunAsync(_catalog.Object, _settings.Object, new CatalogStartupUpdater(_updates.Object, _log), env, _log);

    private List<string> Messages => _log.GetEntries().Select(e => e.Message).ToList();

    [Fact]
    public async Task Follows_the_channel_the_installer_saved()
    {
        SavedChannel("Preview");

        (await Resolve()).Should().Be((CatalogChannel.Preview, CatalogChannelSource.Setting));
    }

    [Fact]
    public async Task Environment_channel_wins_over_the_saved_one()
    {
        SavedChannel("Preview");

        (await Resolve(env: "stable")).Should().Be((CatalogChannel.Stable, CatalogChannelSource.Environment));
    }

    [Fact]
    public async Task Nothing_saved_falls_back_to_Stable_as_the_default()
    {
        (await Resolve()).Should().Be((CatalogChannel.Stable, CatalogChannelSource.Default));
    }

    [Fact]
    public async Task Never_writes_the_user_settings()
    {
        await Run();

        _settings.Verify(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);
        _settings.Verify(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failing_settings_read_falls_back_to_the_default_with_a_warning()
    {
        _settings.Setup(s => s.GetByUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("locked"));

        (await Resolve()).Should().Be((CatalogChannel.Stable, CatalogChannelSource.Default));
        _log.GetEntries().Should().Contain(e => e.Severity == ActivitySeverity.Warning);
    }

    [Fact]
    public void Logs_where_the_catalog_came_from_and_its_versions()
    {
        Report();

        Messages.Should().Contain(@"Catalog: Installed at C:\catalog (seeded from embedded)");
        Messages.Should().Contain("Catalog version: workloads v5, workflows v4, channel Stable");
        _log.GetEntries().Should().OnlyContain(e => e.Source == CatalogStartup.LogSource);
    }

    [Fact]
    public void A_section_without_state_reads_n_a_not_an_empty_version()
    {
        _catalog.SetupGet(c => c.State).Returns(new LocalCatalogState
        {
            Workloads = new SectionState(5, "abc", DateTimeOffset.UnixEpoch)
        });

        Report();

        Messages.Should().Contain("Catalog version: workloads v5, workflows n/a, channel Stable");
    }

    [Fact]
    public void Under_an_override_the_versions_are_labelled_as_the_installed_catalogs()
    {
        // State always describes the installed catalog, never the checkout being read.
        _catalog.SetupGet(c => c.Source).Returns(new CatalogSource(CatalogSourceKind.Override, @"C:\checkout", SeededFromEmbedded: false));

        Report();

        Messages.Should().Contain(@"Catalog: Override at C:\checkout");
        Messages.Should().NotContain(m => m.StartsWith("Catalog version:"));
        Messages.Should().Contain("Installed catalog (not in use while the override is active): workloads v5, workflows v4, channel Stable");
    }

    [Fact]
    public void Logs_one_entry_per_diagnostic_with_its_code_and_severity()
    {
        _catalog.SetupGet(c => c.Diagnostics).Returns(
        [
            new CatalogDiagnostic(CatalogDiagnosticSeverity.Error, CatalogDiagnosticCodes.UnsupportedSchema, "schema 2 is newer"),
            new CatalogDiagnostic(CatalogDiagnosticSeverity.Warning, CatalogDiagnosticCodes.OverrideMissing, "override gone")
        ]);

        Report();

        _log.GetEntries().Should().ContainSingle(e =>
            e.Severity == ActivitySeverity.Error && e.Message.Contains("CAT001") && e.Message.Contains("schema 2 is newer"));
        _log.GetEntries().Should().ContainSingle(e =>
            e.Severity == ActivitySeverity.Warning && e.Message.Contains("CATL001") && e.Message.Contains("override gone"));
    }

    [Fact]
    public void A_failing_catalog_is_logged_and_never_throws()
    {
        _catalog.SetupGet(c => c.Source).Throws(new IOException("disk on fire"));

        var act = () => Report();

        act.Should().NotThrow();
        _log.GetEntries().Should().Contain(e => e.Severity == ActivitySeverity.Error);
    }

    [Fact]
    public async Task Run_checks_the_resolved_channel()
    {
        SavedChannel("Preview");

        await Run();

        _updates.Verify(u => u.CheckAsync(CatalogChannel.Preview, It.IsAny<CancellationToken>()), Times.Once);
        Messages.Should().Contain(m => m.StartsWith("Catalog version:") && m.EndsWith("channel Preview"));
    }

    [Fact]
    public async Task Run_still_checks_for_updates_when_the_catalog_report_fails()
    {
        _catalog.SetupGet(c => c.Source).Throws(new IOException("disk on fire"));

        var act = () => Run();

        await act.Should().NotThrowAsync();
        _updates.Verify(u => u.CheckAsync(CatalogChannel.Stable, It.IsAny<CancellationToken>()), Times.Once);
    }
}
