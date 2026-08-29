using System.Reflection;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the Browse Civitai tab's "Show" flyout: four positively-phrased toggles
/// (Installed / Early Access / Paywalled / NSFW), all ticked by default, that replaced
/// the old "Hide Installed" / "Hide Early Access" / "Show NSFW Content" checkboxes.
/// Each toggle hides exactly what its badge marks
/// (<see cref="CivitaiResultViewModel.IsInstalled"/>, <see cref="CivitaiResultViewModel.ShowEarlyAccessBadge"/>,
/// <see cref="CivitaiResultViewModel.IsPermanentlyPaid"/>, <see cref="CivitaiResultViewModel.IsNsfw"/>).
/// Before this test class there was zero coverage of the client-side filter predicate.
/// </summary>
public sealed class CivitaiBrowserResultVisibilityFilterTests
{
    private static readonly DateTimeOffset Future = DateTimeOffset.UtcNow.AddDays(7);

    private static readonly MethodInfo ApplyClientSideFiltersMethod = typeof(CivitaiBrowserViewModel)
        .GetMethod("ApplyClientSideFilters", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static CivitaiBrowserViewModel CreateVm()
    {
        // Persist paths redirected into a temp dir so the test never reads or clobbers the
        // real LocalAppData queue/waitlist snapshots (see CivitaiBrowserClearQueueTests).
        var tempDir = Directory.CreateTempSubdirectory("dn-show-filter-tests").FullName;
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(tempDir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null,
            persistPathOverride: Path.Combine(tempDir, "waitlist.json"));
        // civitaiClient: null — nothing in this file drives a search, but the null client
        // keeps any accidental one a harmless no-op instead of a real HTTP call.
        return new CivitaiBrowserViewModel(null, null, null, queue, waitlist, null);
    }

    /// <summary>
    /// Builds a bare result card with just the flags this predicate reads.
    /// <see cref="CivitaiResultViewModel.IsInstalled"/> is set separately by the caller —
    /// it comes from <c>ApplyInstalledIndex</c> in production, not from the model.
    /// </summary>
    private static CivitaiResultViewModel Card(
        int id,
        bool nsfw = false,
        DateTimeOffset? eaDeadline = null,
        bool? permanentPaid = null)
    {
        var version = new CivitaiModelVersion
        {
            Id = id * 10,
            Name = "v1",
            BaseModel = "SDXL 1.0",
            EarlyAccessDeadline = eaDeadline,
            PaidAccess = permanentPaid is null && eaDeadline is null
                ? null
                : new CivitaiPaidAccess { Permanent = permanentPaid, EndsAt = eaDeadline }
        };
        var model = new CivitaiModel
        {
            Id = id,
            Name = $"Model {id}",
            Nsfw = nsfw,
            ModelVersions = [version],
        };
        return new CivitaiResultViewModel(model, showNsfwPreviews: false);
    }

    private static void ApplyFilters(CivitaiBrowserViewModel vm) => ApplyClientSideFiltersMethod.Invoke(vm, null);

    [Fact]
    public void AllTicked_HidesNothing()
    {
        var vm = CreateVm();
        var installed = Card(1);
        installed.IsInstalled = true;
        var temporaryEa = Card(2, eaDeadline: Future);
        var permanentlyPaid = Card(3, permanentPaid: true);
        var nsfw = Card(4, nsfw: true);
        foreach (var card in new[] { installed, temporaryEa, permanentlyPaid, nsfw })
            vm.Results.Add(card);

        ApplyFilters(vm);

        vm.Results.Should().OnlyContain(r => !r.IsHidden,
            "every Show toggle defaults to ticked, so nothing should be hidden");
    }

    [Fact]
    public void UntickingShowInstalled_HidesOnlyInstalledCards()
    {
        var vm = CreateVm();
        var installed = Card(1);
        installed.IsInstalled = true;
        var notInstalled = Card(2);
        vm.Results.Add(installed);
        vm.Results.Add(notInstalled);

        vm.ShowInstalled = false;

        installed.IsHidden.Should().BeTrue();
        notInstalled.IsHidden.Should().BeFalse();
    }

    /// <summary>
    /// The whole point of splitting "Early Access" from "Paywalled": ShowEarlyAccessBadge
    /// is already <c>IsEarlyAccess &amp;&amp; !IsPermanentlyPaid</c> (disjoint from IsPermanentlyPaid),
    /// so unticking Early Access must NOT hide a permanently-paid card — only the temporary
    /// EA card. Filtering on the raw IsEarlyAccess flag instead would make Paywalled a
    /// no-op for exactly this card.
    /// </summary>
    [Fact]
    public void UntickingShowEarlyAccess_HidesTheTemporaryCard_NotThePermanentlyPaidCard()
    {
        var vm = CreateVm();
        var temporaryEa = Card(1, eaDeadline: Future);
        var permanentlyPaid = Card(2, permanentPaid: true);
        vm.Results.Add(temporaryEa);
        vm.Results.Add(permanentlyPaid);

        vm.ShowEarlyAccess = false;

        temporaryEa.IsHidden.Should().BeTrue();
        permanentlyPaid.IsHidden.Should().BeFalse(
            "a permanently-paid card shows the Paywalled badge, not Early Access — its own toggle governs it");
    }

    /// <summary>Mirror of the test above: Paywalled hides the permanently-paid card and leaves
    /// the temporary Early Access card alone.</summary>
    [Fact]
    public void UntickingShowPaywalled_HidesThePermanentlyPaidCard_NotTheTemporaryEaCard()
    {
        var vm = CreateVm();
        var temporaryEa = Card(1, eaDeadline: Future);
        var permanentlyPaid = Card(2, permanentPaid: true);
        vm.Results.Add(temporaryEa);
        vm.Results.Add(permanentlyPaid);

        vm.ShowPaywalled = false;

        permanentlyPaid.IsHidden.Should().BeTrue();
        temporaryEa.IsHidden.Should().BeFalse(
            "a temporary Early Access card is not permanently paid — its own toggle governs it");
    }

    [Fact]
    public void UntickingShowNsfw_HidesOnlyNsfwCards()
    {
        var vm = CreateVm();
        var nsfw = Card(1, nsfw: true);
        var safe = Card(2);
        vm.Results.Add(nsfw);
        vm.Results.Add(safe);

        vm.ShowNsfw = false;

        nsfw.IsHidden.Should().BeTrue();
        safe.IsHidden.Should().BeFalse();
    }

    [Fact]
    public void TickingBackShowInstalled_UnhidesTheCard()
    {
        var vm = CreateVm();
        var installed = Card(1);
        installed.IsInstalled = true;
        vm.Results.Add(installed);
        vm.ShowInstalled = false;
        installed.IsHidden.Should().BeTrue();

        vm.ShowInstalled = true;

        installed.IsHidden.Should().BeFalse();
    }

    [Fact]
    public void ActiveShowFilterCount_CountsUntickedToggles_AndDefaultsToZero()
    {
        var vm = CreateVm();
        vm.ActiveShowFilterCount.Should().Be(0);
        vm.IsShowFilterActive.Should().BeFalse();

        vm.ShowInstalled = false;
        vm.ShowNsfw = false;

        vm.ActiveShowFilterCount.Should().Be(2);
        vm.IsShowFilterActive.Should().BeTrue();
    }

    [Fact]
    public void ShowAllResultFiltersCommand_ReticksEverything()
    {
        var vm = CreateVm();
        vm.ShowInstalled = false;
        vm.ShowEarlyAccess = false;
        vm.ShowPaywalled = false;
        vm.ShowNsfw = false;

        vm.ShowAllResultFiltersCommand.Execute(null);

        vm.ShowInstalled.Should().BeTrue();
        vm.ShowEarlyAccess.Should().BeTrue();
        vm.ShowPaywalled.Should().BeTrue();
        vm.ShowNsfw.Should().BeTrue();
        vm.IsShowFilterActive.Should().BeFalse();
    }
}
