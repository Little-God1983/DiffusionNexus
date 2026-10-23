using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using DiffusionNexus.UI.Views.Dialogs;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the pre-download dialog-choice handling: waitlisting temporary-EA picks
/// (permanent ones are skipped — they never become free), opening the Civitai pages,
/// skip/download-anyway, and the already-installed group that shares the same single
/// prompt so a mixed selection never produces two dialogs.
/// </summary>
public sealed class DownloadPreflightChoiceTests : IDisposable
{
    /// <summary>
    /// The fixtures' "now", anchored to today's date rather than a fixed calendar day — see the
    /// same field on <see cref="CivitaiWaitlistTests"/> for why. Here the rot shows up differently:
    /// an expired deadline stops a version counting as early-access, so it is enqueued instead of
    /// opened, and the assertion fails on an empty collection rather than a wrong value.
    /// </summary>
    private static readonly DateTimeOffset Now =
        new(DateTime.UtcNow.Date.AddHours(10), TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-ea-choice").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private (CivitaiBrowserViewModel Vm, CivitaiWaitlist Waitlist, CivitaiDownloadQueue Queue, List<string> Opened) Create()
    {
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(_tempDir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null,
            persistPathOverride: Path.Combine(_tempDir, "waitlist.json"));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue, waitlist, null);
        var opened = new List<string>();
        vm.UrlOpener = opened.Add;
        return (vm, waitlist, queue, opened);
    }

    private static List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> MixedPairs()
    {
        var free = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(1, deadline: null), modelId: 101, name: "Free LoRA");
        var tempEa = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(2, Now.AddDays(7)), modelId: 102, name: "EA LoRA");
        var permanent = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(3, deadline: null, permanent: true), modelId: 103, name: "Paid LoRA");
        return [free, tempEa, permanent];
    }

    [Fact]
    public void AddToWaitlist_QueuesFreeItems_WaitlistsTempEa_SkipsPermanent()
    {
        var (vm, waitlist, queue, _) = Create();

        vm.ApplyPreflightChoice(DownloadPreflightResult.AddToWaitlist, MixedPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1, "non-EA picks download immediately");
        waitlist.Entries.Should().ContainSingle(e => e.VersionId == 2, "temporary EA is waitlistable");
        waitlist.Entries.Should().NotContain(e => e.VersionId == 3, "permanently paid never becomes free");
        vm.StatusMessage.Should().Contain("permanently paid");
    }

    [Fact]
    public void OpenWebsite_QueuesFreeItems_OpensOnePageDistinctPerEaModel()
    {
        var (vm, _, queue, opened) = Create();

        vm.ApplyPreflightChoice(DownloadPreflightResult.OpenWebsite, MixedPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1);
        opened.Should().BeEquivalentTo(
            "https://civitai.com/models/102",
            "https://civitai.com/models/103");
    }

    [Fact]
    public void OpenWebsite_TwoEaVersionsOfSameModel_OpensOnlyOnePage()
    {
        // Same model, two EA versions selected — mirrors how AddSelectionToQueueAsync
        // reuses one CivitaiResultViewModel reference across multiple picks.
        var (vm, _, _, opened) = Create();

        var model = new CivitaiModel
        {
            Id = 202,
            Name = "Multi-version LoRA",
            ModelVersions =
            [
                CivitaiWaitlistTests.Version(20, Now.AddDays(3)),
                CivitaiWaitlistTests.Version(21, Now.AddDays(5))
            ]
        };
        var result = new CivitaiResultViewModel(model, showNsfwPreviews: false);
        List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> pairs =
            [.. result.Versions.Select(pick => (result, pick))];

        vm.ApplyPreflightChoice(DownloadPreflightResult.OpenWebsite, pairs);

        opened.Should().ContainSingle().Which.Should().Be("https://civitai.com/models/202");
    }

    [Fact]
    public void OpenWebsite_NsfwModel_OpensCivitaiRedHost()
    {
        var (vm, _, _, opened) = Create();

        var model = new CivitaiModel
        {
            Id = 303,
            Name = "NSFW LoRA",
            Nsfw = true,
            ModelVersions = [CivitaiWaitlistTests.Version(30, Now.AddDays(2))]
        };
        var result = new CivitaiResultViewModel(model, showNsfwPreviews: false);
        result.IsNsfw.Should().BeTrue("model.Nsfw=true should flag the card as NSFW under CivitaiNsfwPolicy");
        List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> pairs =
            [(result, result.Versions[0])];

        vm.ApplyPreflightChoice(DownloadPreflightResult.OpenWebsite, pairs);

        opened.Should().ContainSingle().Which.Should().StartWith("https://civitai.red/");
    }

    [Fact]
    public void SkipFlagged_QueuesOnlyUnflagged()
    {
        var (vm, waitlist, queue, _) = Create();

        vm.ApplyPreflightChoice(DownloadPreflightResult.SkipFlagged, MixedPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1);
        waitlist.Entries.Should().BeEmpty();
    }

    [Fact]
    public void DownloadAnyway_QueuesEverything()
    {
        var (vm, _, queue, _) = Create();

        vm.ApplyPreflightChoice(DownloadPreflightResult.DownloadAnyway, MixedPairs());

        queue.Jobs.Should().HaveCount(3);
    }

    [Fact]
    public void Cancel_DoesNothing()
    {
        var (vm, waitlist, queue, opened) = Create();

        vm.ApplyPreflightChoice(DownloadPreflightResult.Cancel, MixedPairs());

        queue.Jobs.Should().BeEmpty();
        waitlist.Entries.Should().BeEmpty();
        opened.Should().BeEmpty();
    }

    [Fact]
    public void PickItem_ExposesPermanentFlag()
    {
        var (_, pick) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(9, deadline: null, permanent: true));
        pick.IsPermanentlyPaid.Should().BeTrue();

        var (_, tempPick) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(10, Now.AddDays(7)));
        tempPick.IsPermanentlyPaid.Should().BeFalse();
    }

    /// <summary>Free version 1, temporary-EA version 2, and free-but-already-owned version 4.</summary>
    private static List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> WithInstalledPairs()
    {
        var free = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(1, deadline: null), modelId: 101, name: "Free LoRA");
        var tempEa = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(2, Now.AddDays(7)), modelId: 102, name: "EA LoRA");
        var installed = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(4, deadline: null), modelId: 104, name: "Owned LoRA");
        installed.Pick.IsInstalled = true;
        return [free, tempEa, installed];
    }

    [Fact]
    public void SkipFlagged_SkipsAlreadyInstalledAlongsideEa()
    {
        var (vm, _, queue, _) = Create();

        vm.ApplyPreflightChoice(DownloadPreflightResult.SkipFlagged, WithInstalledPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1, "only the unflagged version downloads");
        vm.StatusMessage.Should().Contain("already installed");
    }

    [Fact]
    public void DownloadAnyway_ReDownloadsInstalledVersions()
    {
        var (vm, _, queue, _) = Create();

        vm.ApplyPreflightChoice(DownloadPreflightResult.DownloadAnyway, WithInstalledPairs());

        queue.Jobs.Should().HaveCount(3, "the user explicitly chose to fetch everything again");
        queue.Jobs.Should().Contain(j => j.VersionId == 4);
    }

    [Fact]
    public void AddToWaitlist_LeavesInstalledVersionsAlone()
    {
        var (vm, waitlist, queue, _) = Create();

        vm.ApplyPreflightChoice(DownloadPreflightResult.AddToWaitlist, WithInstalledPairs());

        waitlist.Entries.Should().ContainSingle(e => e.VersionId == 2);
        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1,
            "the cautious branch must not re-fetch a file the user already owns");
        vm.StatusMessage.Should().Contain("already-installed");
    }

    [Fact]
    public void OpenWebsite_DoesNotReDownloadInstalledVersions()
    {
        var (vm, _, queue, opened) = Create();

        vm.ApplyPreflightChoice(DownloadPreflightResult.OpenWebsite, WithInstalledPairs());

        opened.Should().ContainSingle().Which.Should().Be("https://civitai.com/models/102");
        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1);
    }

    [Fact]
    public void PickBothPaidAndInstalled_IsTreatedAsPaidOnly()
    {
        // A version can be early access AND already owned. It must land in exactly one
        // group, or the dialog double-counts it and "add the rest" loses an item.
        var (vm, waitlist, queue, _) = Create();

        var pair = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(5, Now.AddDays(4)), modelId: 105, name: "Owned EA LoRA");
        pair.Pick.IsInstalled = true;

        vm.ApplyPreflightChoice(DownloadPreflightResult.AddToWaitlist, [pair]);

        waitlist.Entries.Should().ContainSingle(e => e.VersionId == 5, "the paywall is the decisive flag");
        queue.Jobs.Should().BeEmpty();
    }
}
