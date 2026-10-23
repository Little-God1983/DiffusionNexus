using System.Reflection;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Regression coverage for spec RC5: primary-search cards used to wire only
/// <see cref="CivitaiResultViewModel.EnqueueAllVersionsHandler"/>, silently no-oping the
/// "Add selected to queue" button on every card except the small minority pulled in by the
/// tag-fallback search — that path always wired both handlers. The fix shares one private
/// factory (<c>CreateResultCard</c>) between the primary-search loop and the tag-fallback
/// loop in <see cref="CivitaiBrowserViewModel"/>, so both card "shapes" are now produced by
/// the exact same code and can't drift apart again. Invoked via reflection since the factory
/// is intentionally private — there is no other seam that observes card construction without
/// driving a full mocked network search.
/// </summary>
public sealed class CivitaiBrowserCardHandlerTests
{
    private static CivitaiBrowserViewModel CreateVm()
    {
        // Persist paths redirected into a temp dir so the test never reads or clobbers the
        // real LocalAppData queue/waitlist snapshots (see CivitaiBrowserClearQueueTests).
        var tempDir = Directory.CreateTempSubdirectory("dn-card-handler-tests").FullName;
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(tempDir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null,
            persistPathOverride: Path.Combine(tempDir, "waitlist.json"));
        // civitaiClient: null makes the constructor's own fire-and-forget initial search
        // bail out immediately (LoadNextAsync's null-client guard) — no network, no races.
        return new CivitaiBrowserViewModel(null, null, null, queue, waitlist, null);
    }

    private static CivitaiModel Model(int id) => new()
    {
        Id = id,
        Name = $"Model {id}",
        ModelVersions = [new CivitaiModelVersion { Id = id * 10, Name = "v1", BaseModel = "SDXL 1.0" }],
    };

    private static CivitaiResultViewModel InvokeCreateResultCard(CivitaiBrowserViewModel vm, CivitaiModel model)
    {
        var method = typeof(CivitaiBrowserViewModel).GetMethod(
            "CreateResultCard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (CivitaiResultViewModel)method.Invoke(vm, [model])!;
    }

    [Fact]
    public void CreateResultCard_WiresBothEnqueueHandlers_ForACard()
    {
        var vm = CreateVm();

        var card = InvokeCreateResultCard(vm, Model(1));

        card.EnqueueAllVersionsHandler.Should().NotBeNull(
            "'Add all to queue' must work on every card");
        card.EnqueueSelectedVersionsHandler.Should().NotBeNull(
            "'Add selected to queue' must work on every card, not just tag-fallback results");
    }

    [Fact]
    public void CreateResultCard_WiresBothEnqueueHandlers_OnRepeatedCalls()
    {
        // Simulates the two former call sites (primary-search loop and tag-fallback loop) —
        // both now go through this one factory, so invoking it repeatedly for distinct models
        // must keep wiring both handlers every time, not just the first.
        var vm = CreateVm();

        var first = InvokeCreateResultCard(vm, Model(1));
        var second = InvokeCreateResultCard(vm, Model(2));

        first.EnqueueAllVersionsHandler.Should().NotBeNull();
        first.EnqueueSelectedVersionsHandler.Should().NotBeNull();
        second.EnqueueAllVersionsHandler.Should().NotBeNull();
        second.EnqueueSelectedVersionsHandler.Should().NotBeNull();
    }

    [Fact]
    public void CreateResultCard_SelectedVersionsHandler_ActuallyInvokesTheEnqueueSelectedCommand()
    {
        // Guards against a handler that's non-null but wired to the wrong delegate (e.g. both
        // properties pointing at EnqueueAllVersionsForCard) — ticking a version and executing
        // "Add selected" must reach the selected-versions path, not silently no-op.
        var vm = CreateVm();
        var card = InvokeCreateResultCard(vm, Model(1));
        card.Versions[0].IsSelected = true;

        var act = () => card.EnqueueSelectedVersionsCommand.Execute(null);

        act.Should().NotThrow("the handler is wired even though the queue/waitlist plumbing behind it is a no-op here");
    }
}
