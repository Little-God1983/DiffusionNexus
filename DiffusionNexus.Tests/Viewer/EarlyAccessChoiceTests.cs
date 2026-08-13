using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using DiffusionNexus.UI.Views.Dialogs;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the dialog-choice handling for early-access selections: waitlisting
/// temporary-EA picks (permanent ones are skipped — they never become free),
/// opening the Civitai pages, and the pre-existing skip/add-anyway paths.
/// </summary>
public sealed class EarlyAccessChoiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
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

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.AddToWaitlist, MixedPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1, "non-EA picks download immediately");
        waitlist.Entries.Should().ContainSingle(e => e.VersionId == 2, "temporary EA is waitlistable");
        waitlist.Entries.Should().NotContain(e => e.VersionId == 3, "permanently paid never becomes free");
        vm.StatusMessage.Should().Contain("permanently paid");
    }

    [Fact]
    public void OpenWebsite_QueuesFreeItems_OpensOnePageDistinctPerEaModel()
    {
        var (vm, _, queue, opened) = Create();

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.OpenWebsite, MixedPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1);
        opened.Should().BeEquivalentTo(
            "https://civitai.com/models/102",
            "https://civitai.com/models/103");
    }

    [Fact]
    public void SkipEarlyAccess_QueuesOnlyNonEa()
    {
        var (vm, waitlist, queue, _) = Create();

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.SkipEarlyAccess, MixedPairs());

        queue.Jobs.Should().ContainSingle(j => j.VersionId == 1);
        waitlist.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AddAnyway_QueuesEverything()
    {
        var (vm, _, queue, _) = Create();

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.AddAnyway, MixedPairs());

        queue.Jobs.Should().HaveCount(3);
    }

    [Fact]
    public void Cancel_DoesNothing()
    {
        var (vm, waitlist, queue, opened) = Create();

        vm.ApplyEarlyAccessChoice(EarlyAccessConfirmResult.Cancel, MixedPairs());

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
}
