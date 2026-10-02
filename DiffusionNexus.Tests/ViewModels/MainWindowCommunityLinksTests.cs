using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The sidebar's community links: seeded from the SDK's compiled-in list, replaced by the remote
/// one when it lands, and left alone when the fetch fails. Expectations come from the slot rule
/// or a fixed list, never from how many defaults the current SDK happens to ship.
/// </summary>
public sealed class MainWindowCommunityLinksTests
{
    /// <summary>Five rows: one more than the slots, so the list always overflows.</summary>
    private static readonly CommunityLink[] FiveLinks =
    [
        new("One", "https://example.com/1", "youtube"),
        new("Two", "https://example.com/2", "patreon"),
        new("Three", "https://example.com/3", "globe"),
        new("Four", "https://example.com/4", "mail"),
        new("Five", "https://example.com/5", "linktree"),
    ];

    private static IEnumerable<string> Names(IEnumerable<CommunityLinkItemViewModel> items) => items.Select(i => i.Name);

    private static IEnumerable<string> Names(IEnumerable<CommunityLink> links) => links.Select(l => l.Name);

    private static Mock<ICommunityLinksService> ServiceReturning(CommunityLinksResult result)
    {
        var service = new Mock<ICommunityLinksService>();
        service.Setup(s => s.GetLinksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return service;
    }

    [Fact]
    public void NewViewModel_ShowsTheCompiledInList_SplitPerTheSlotRule()
    {
        var (inline, overflow) = CommunityLinkSlots.Split(CommunityLink.Defaults);

        var vm = new DiffusionNexusMainWindowViewModel();

        Names(vm.InlineCommunityLinks).Should().Equal(Names(inline));
        Names(vm.OverflowCommunityLinks).Should().Equal(Names(overflow));
        vm.HasCommunityLinkOverflow.Should().Be(overflow.Count > 0);
        vm.CommunityLinkOverflowLabel.Should().Be($"More ({overflow.Count})");
    }

    [Fact]
    public void ApplyCommunityLinks_PutsTheRestBehindMore_WhenTheListOverflows()
    {
        var vm = new DiffusionNexusMainWindowViewModel();

        vm.ApplyCommunityLinks(FiveLinks);

        Names(vm.InlineCommunityLinks).Should().Equal("One", "Two", "Three");
        Names(vm.OverflowCommunityLinks).Should().Equal("Four", "Five");
        vm.HasCommunityLinkOverflow.Should().BeTrue();
        vm.CommunityLinkOverflowLabel.Should().Be("More (2)");
    }

    [Fact]
    public async Task Load_ReplacesTheListWithTheRemoteOne_AndDropsMoreWhenItFits()
    {
        var vm = new DiffusionNexusMainWindowViewModel();
        vm.ApplyCommunityLinks(FiveLinks);
        var remote = new[]
        {
            new CommunityLink("Discord", "https://discord.gg/example", "chat"),
            new CommunityLink("YouTube", "https://www.youtube.com/@IntoTheLatent", "youtube"),
        };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var logger = new Mock<IUnifiedLogger>();

        await vm.LoadCommunityLinksAsync(
            ServiceReturning(new CommunityLinksResult(remote, IsFallback: false)).Object,
            logger.Object,
            new ImmediateUiScheduler());

        Names(vm.InlineCommunityLinks).Should().Equal("Discord", "YouTube");
        vm.InlineCommunityLinks[0].Icon.Should().Be("chat");
        vm.OverflowCommunityLinks.Should().BeEmpty();
        vm.HasCommunityLinkOverflow.Should().BeFalse();
        changed.Should().Contain(nameof(DiffusionNexusMainWindowViewModel.HasCommunityLinkOverflow),
            "the window closes an open More flyout on this change");
        logger.Verify(l => l.Info(LogCategory.Network, "CommunityLinks",
            It.Is<string>(m => m.Contains("Loaded 2 community links")), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Load_OnFallback_KeepsTheCompiledInList_AndLogsTheReasonWithoutAWarning()
    {
        var vm = new DiffusionNexusMainWindowViewModel();
        var before = vm.InlineCommunityLinks.ToList();
        var logger = new Mock<IUnifiedLogger>();
        var scheduler = new ImmediateUiScheduler();

        await vm.LoadCommunityLinksAsync(
            ServiceReturning(CommunityLinksResult.Fallback("Timed out after 3s")).Object,
            logger.Object,
            scheduler);

        vm.InlineCommunityLinks.Should().Equal(before, "a failed fetch must not rebuild the sidebar");
        scheduler.InvokeCount.Should().Be(0);
        logger.Verify(l => l.Info(LogCategory.Network, "CommunityLinks",
            It.IsAny<string>(), "Timed out after 3s"), Times.Once);
        logger.Verify(l => l.Warn(It.IsAny<LogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never, "offline is normal and must not light the status bar every launch");
    }

    [Fact]
    public async Task Load_WhenTheServiceThrows_KeepsTheCompiledInList()
    {
        var vm = new DiffusionNexusMainWindowViewModel();
        var before = Names(vm.InlineCommunityLinks).ToList();
        var service = new Mock<ICommunityLinksService>();
        service.Setup(s => s.GetLinksAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("boom"));

        var act = () => vm.LoadCommunityLinksAsync(service.Object, logger: null, new ImmediateUiScheduler());

        await act.Should().NotThrowAsync();
        Names(vm.InlineCommunityLinks).Should().Equal(before);
    }

    [Fact]
    public async Task Load_WhenTheSwapThrows_DoesNotThrow_AndLogsUnderCommunityLinks()
    {
        var vm = new DiffusionNexusMainWindowViewModel();
        var logger = new Mock<IUnifiedLogger>();
        var scheduler = new Mock<IUiScheduler>();
        scheduler.Setup(s => s.InvokeAsync(It.IsAny<Action>())).ThrowsAsync(new InvalidOperationException("layout pass"));

        var act = () => vm.LoadCommunityLinksAsync(
            ServiceReturning(new CommunityLinksResult(FiveLinks, IsFallback: false)).Object,
            logger.Object,
            scheduler.Object);

        await act.Should().NotThrowAsync();
        logger.Verify(l => l.Warn(LogCategory.Network, "CommunityLinks", It.IsAny<string>(), "layout pass"), Times.Once);
    }

    [Fact]
    public void OpenCommand_OpensTheLinksUrl_InlineAndUnderMore()
    {
        var opened = new List<string>();
        var vm = new DiffusionNexusMainWindowViewModel { OpenExternalUrl = opened.Add };
        vm.ApplyCommunityLinks(FiveLinks);

        vm.InlineCommunityLinks[0].OpenCommand.Execute(null);
        vm.OverflowCommunityLinks[^1].OpenCommand.Execute(null);

        opened.Should().Equal("https://example.com/1", "https://example.com/5");
    }

    [Fact]
    public void OpenCommand_WhenTheBrowserCannotStart_DoesNotThrow()
    {
        var vm = new DiffusionNexusMainWindowViewModel
        {
            OpenExternalUrl = _ => throw new System.ComponentModel.Win32Exception("no browser")
        };
        vm.ApplyCommunityLinks(FiveLinks);

        var act = () => vm.InlineCommunityLinks[0].OpenCommand.Execute(null);

        act.Should().NotThrow();
    }
}
