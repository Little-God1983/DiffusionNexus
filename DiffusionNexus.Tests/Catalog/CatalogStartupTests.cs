using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>
/// The background catalog startup: the coordinator resolves the channel, the Unified Console report, then the
/// coordinator's startup check (#618). Runs off the UI thread because <see cref="ICatalog.Source"/>
/// blocks while the embedded seed unpacks.
/// </summary>
public class CatalogStartupTests
{
    private readonly Mock<ICatalog> _catalog = new();
    private readonly Mock<ICatalogUpdateCoordinator> _coordinator = new();
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
        _coordinator.Setup(c => c.ResolveChannelAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CatalogChannel.Stable);
        _coordinator.SetupGet(c => c.ChannelSource).Returns(CatalogChannelSource.Setting);
    }

    private void Report(CatalogChannel channel = CatalogChannel.Stable, CatalogChannelSource source = CatalogChannelSource.Default)
        => CatalogStartup.Report(_catalog.Object, channel, source, _log);

    private Task Run() => CatalogStartup.RunAsync(_catalog.Object, _coordinator.Object, _log);

    private List<string> Messages => _log.GetEntries().Select(e => e.Message).ToList();

    [Fact]
    public void Logs_where_the_catalog_came_from_and_its_versions()
    {
        Report();

        Messages.Should().Contain(@"Catalog: Installed at C:\catalog (seeded from embedded)");
        Messages.Should().Contain("Catalog version: workloads v5, workflows v4, channel Stable");
        _log.GetEntries().Should().OnlyContain(e => e.Source == CatalogStartup.LogSource);
    }

    [Theory]
    [InlineData(CatalogChannel.Preview, CatalogChannel.Stable, "Preview")]  // a section channel outranks the stamp
    [InlineData(null, CatalogChannel.Preview, "Preview")]                   // SDK 2.0.0 state: only the stamp
    public void The_version_line_shows_the_installed_catalogs_channel_and_the_detail_the_followed_one(
        CatalogChannel? section, CatalogChannel stamp, string expected)
    {
        _catalog.SetupGet(c => c.State).Returns(new LocalCatalogState
        {
            Channel = stamp,
            Workloads = new SectionState(5, "abc", DateTimeOffset.UnixEpoch) { Channel = section },
            Workflows = new SectionState(4, "abc", DateTimeOffset.UnixEpoch) { Channel = section }
        });

        Report(CatalogChannel.Stable, CatalogChannelSource.Default);

        var entry = _log.GetEntries().Single(e => e.Message.StartsWith("Catalog version:"));
        entry.Message.Should().Be($"Catalog version: workloads v5, workflows v4, channel {expected}");
        entry.Details.Should().Be("Following Stable (Default)");
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
    public async Task Run_resolves_the_channel_before_the_catalog_is_read_then_checks()
    {
        var order = new List<string>();
        _coordinator.Setup(c => c.ResolveChannelAsync(It.IsAny<CancellationToken>()))
                    .Callback(() => order.Add("resolve")).ReturnsAsync(CatalogChannel.Preview);
        _catalog.SetupGet(c => c.Source).Callback(() => order.Add("read")).Returns(new CatalogSource(CatalogSourceKind.Installed, @"C:\x", false));
        _coordinator.Setup(c => c.CheckAtStartupAsync(It.IsAny<CancellationToken>()))
                    .Callback(() => order.Add("check")).Returns(Task.CompletedTask);

        await Run();

        order.Should().StartWith(["resolve", "read"]).And.EndWith("check");
        _log.GetEntries().Should().Contain(e => e.Message.StartsWith("Catalog version:") && e.Details == "Following Preview (Setting)");
    }

    [Fact]
    public async Task Run_still_checks_for_updates_when_the_catalog_report_fails()
    {
        _catalog.SetupGet(c => c.Source).Throws(new IOException("locked"));

        var act = () => Run();

        await act.Should().NotThrowAsync();
        _coordinator.Verify(c => c.CheckAtStartupAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
