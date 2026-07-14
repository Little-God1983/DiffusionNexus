using DiffusionNexus.UI.Services.Startup;

namespace DiffusionNexus.Tests.Services;

public class StartupProgressServiceTests
{
    private static StartupProgressService NewService(bool canvas = false)
        => new(StartupProgressService.BuildDefaultChecks(includeDiffusionCanvas: canvas));

    [Fact]
    public void DefaultChecks_HaveExpectedOrderAndGating()
    {
        var svc = NewService();
        Assert.Equal(
            new[] { "database", "installer-manager", "lora-dataset-helper", "lora-viewer",
                    "generation-gallery", "image-comparer", "workflows", "settings",
                    "diffusion-engine", "updates" },
            svc.Checks.Select(c => c.Id).ToArray());
        Assert.All(svc.Checks.Where(c => c.Id != "updates"), c => Assert.True(c.GatesReadiness));
        Assert.False(svc.Checks.Single(c => c.Id == "updates").GatesReadiness);
    }

    [Fact]
    public void CanvasFlag_InsertsDiffusionCanvasAfterGenerationGallery()
    {
        var svc = NewService(canvas: true);
        var ids = svc.Checks.Select(c => c.Id).ToList();
        Assert.Equal(ids.IndexOf("generation-gallery") + 1, ids.IndexOf("diffusion-canvas"));
    }

    [Fact]
    public void BeginCompleteFail_TransitionStatesAndRaiseEvents()
    {
        var svc = NewService();
        var raised = new List<(string Id, StartupCheckState State)>();
        svc.CheckChanged += c => raised.Add((c.Id, c.State));

        svc.Begin("database");
        svc.Complete("database");
        svc.Begin("settings");
        svc.Fail("settings", "boom");

        Assert.Equal(StartupCheckState.Done, svc.Checks.Single(c => c.Id == "database").State);
        var settings = svc.Checks.Single(c => c.Id == "settings");
        Assert.Equal(StartupCheckState.Failed, settings.State);
        Assert.Equal("boom", settings.Error);
        Assert.Equal(
            new[] { ("database", StartupCheckState.Running), ("database", StartupCheckState.Done),
                    ("settings", StartupCheckState.Running), ("settings", StartupCheckState.Failed) },
            raised.ToArray());
    }

    [Fact]
    public void CoreChecksTerminal_IgnoresUpdates_AndCountsFailedAsTerminal()
    {
        var svc = NewService();
        foreach (var c in svc.Checks.Where(c => c.GatesReadiness))
        {
            Assert.False(svc.CoreChecksTerminal);
            svc.Begin(c.Id);
            if (c.Id == "lora-viewer") svc.Fail(c.Id, "x"); else svc.Complete(c.Id);
        }
        Assert.True(svc.CoreChecksTerminal); // "updates" still Pending — must not gate
    }

    [Fact]
    public void UiReady_CompletesOnceOnSignal()
    {
        var svc = NewService();
        Assert.False(svc.UiReady.IsCompleted);
        svc.SignalUiReady();
        svc.SignalUiReady(); // idempotent, must not throw
        Assert.True(svc.UiReady.IsCompletedSuccessfully);
    }

    [Fact]
    public void UnknownId_Throws()
    {
        var svc = NewService();
        Assert.Throws<ArgumentException>(() => svc.Begin("nope"));
    }
}
