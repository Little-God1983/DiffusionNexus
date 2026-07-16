namespace DiffusionNexus.UI.Services.Startup;

/// <summary>
/// Ordered startup ready-check list (spec 2026-07-15). Plain C# — no Avalonia —
/// so it is unit-testable and usable from any startup phase. CheckChanged is
/// raised synchronously on the caller's thread; all production callers are
/// UI-thread startup phases, so the overlay ViewModel needs no marshaling.
/// </summary>
public sealed class StartupProgressService
{
    private readonly List<StartupCheck> _checks;
    private readonly TaskCompletionSource _uiReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupProgressService(IReadOnlyList<StartupCheck> checks)
        => _checks = [.. checks];

    public IReadOnlyList<StartupCheck> Checks => _checks;

    public event Action<StartupCheck>? CheckChanged;

    /// <summary>Completed by the app after the overlay's dispatcher-drain sentinel
    /// has run — i.e., when the UI is provably responsive. Deferred background
    /// work (autocomplete trie) starts on this signal.</summary>
    public Task UiReady => _uiReady.Task;

    public bool CoreChecksTerminal => _checks
        .Where(c => c.GatesReadiness)
        .All(c => c.State is StartupCheckState.Done or StartupCheckState.Failed);

    public void Begin(string id) => Set(id, StartupCheckState.Running, null);
    public void Complete(string id) => Set(id, StartupCheckState.Done, null);
    public void Fail(string id, string message) => Set(id, StartupCheckState.Failed, message);

    public void SignalUiReady() => _uiReady.TrySetResult();

    private void Set(string id, StartupCheckState state, string? error)
    {
        var check = _checks.FirstOrDefault(c => c.Id == id)
            ?? throw new ArgumentException($"Unknown startup check '{id}'.", nameof(id));
        check.State = state;
        check.Error = error;
        CheckChanged?.Invoke(check);
    }

    /// <summary>Single source of truth for the check list. Module entries MUST match
    /// the registration order in App.RegisterModulesAsync (Task 4).</summary>
    public static IReadOnlyList<StartupCheck> BuildDefaultChecks(bool includeDiffusionCanvas)
    {
        var checks = new List<StartupCheck>
        {
            new() { Id = "database", DisplayName = "Database" },
            new() { Id = "installer-manager", DisplayName = "Installer Manager" },
            new() { Id = "lora-dataset-helper", DisplayName = "LoRA Dataset Helper" },
            new() { Id = "lora-viewer", DisplayName = "LoRA Viewer" },
            new() { Id = "generation-gallery", DisplayName = "Generation Gallery" },
        };
        if (includeDiffusionCanvas)
            checks.Add(new() { Id = "diffusion-canvas", DisplayName = "Diffusion Canvas" });
        checks.AddRange(
        [
            new StartupCheck { Id = "image-comparer", DisplayName = "Image Comparer" },
            new StartupCheck { Id = "workflows", DisplayName = "Workflows" },
            new StartupCheck { Id = "settings", DisplayName = "Settings" },
            new StartupCheck { Id = "diffusion-engine", DisplayName = "Diffusion Engine" },
            new StartupCheck { Id = "updates", DisplayName = "Updates", GatesReadiness = false },
        ]);
        return checks;
    }
}
