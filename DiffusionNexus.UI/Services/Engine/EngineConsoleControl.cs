using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Start, Stop and Restart for the Engine's tile in the Unified Console's instance row (#610). The
/// Engine's row has no executable, so the generic package launch did nothing for it. Never throws:
/// what goes wrong is logged to the console under the Engine's name, where the tile filters to.
/// </summary>
public sealed class EngineConsoleControl
{
    private const string Source = "Diffusion Nexus Engine";

    private readonly IManagedEngineControl _engine;
    private readonly IEngineRootResolver _roots;
    private readonly IDialogService? _dialogs;
    private readonly IUnifiedLogger _logger;

    public EngineConsoleControl(IManagedEngineControl engine, IEngineRootResolver roots, IDialogService? dialogs, IUnifiedLogger logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(logger);
        _engine = engine;
        _roots = roots;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>Starts the Engine from its install folder; a start that fails says why in the console.</summary>
    public async Task StartAsync()
    {
        try
        {
            var root = await _roots.ResolveAsync();
            if (root is null)
            {
                _logger.Warn(LogCategory.InstanceManagement, Source,
                    "The Engine is not installed. Install it from its tile in the Installer Manager.");
                return;
            }

            var result = await _engine.EnsureRunningAsync(root, CancellationToken.None);
            if (result.IsRunning) return;
            if (result.StoppedByRequest)
                _logger.Info(LogCategory.InstanceManagement, Source, "The Engine start was stopped.");
            else
                _logger.Warn(LogCategory.InstanceManagement, Source,
                    $"The Engine did not start: {result.FailureReason ?? "no reason was given"}");
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.InstanceManagement, Source, $"Could not start the Engine: {ex.Message}", ex);
        }
    }

    private static readonly StopWording Stopping = new(
        "Stop the Engine?", "Stopping it cancels that generation.", "Stop anyway", "stopped");

    private static readonly StopWording Restarting = new(
        "Restart the Engine?", "Restarting it cancels that generation.", "Restart anyway", "restarted");

    private sealed record StopWording(string Title, string Consequence, string Confirm, string Done);

    /// <summary>
    /// Stops the Engine, or aborts its start. While a generation runs on it or is getting under way,
    /// asks first: stopping cancels that generation.
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            await StopAskingIfBusyAsync(Stopping);
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.InstanceManagement, Source, $"Could not stop the Engine: {ex.Message}", ex);
        }
    }

    /// <summary>Stops the Engine and starts it again, asking first while a generation runs.</summary>
    public async Task RestartAsync()
    {
        try
        {
            if (!await StopAskingIfBusyAsync(Restarting)) return;
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.InstanceManagement, Source, $"Could not restart the Engine: {ex.Message}", ex);
            return;
        }

        await StartAsync();
    }

    // Returns whether the Engine stopped. A generation the console can see: the user decides. A start
    // with nothing on it: aborted at once (StopIfIdle would wait out the start). Otherwise the engine
    // decides "idle" under its start lock; a generation that got under way since makes it busy after
    // all, and then the user decides.
    private async Task<bool> StopAskingIfBusyAsync(StopWording wording)
    {
        if (_engine.HasActiveJobs) return await StopIfConfirmedAsync(wording);

        if (_engine.IsStarting)
        {
            await _engine.StopAsync();
            return true;
        }

        return await _engine.StopIfIdleAsync() || await StopIfConfirmedAsync(wording);
    }

    // A dismissed dialog keeps it running. Without a dialog to ask with, it keeps running and says why.
    private async Task<bool> StopIfConfirmedAsync(StopWording wording)
    {
        if (_dialogs is null)
        {
            _logger.Warn(LogCategory.InstanceManagement, Source,
                $"A generation is running on the Engine, so it was not {wording.Done}. Try again when the generation has finished.");
            return false;
        }

        var choice = await _dialogs.ShowOptionsAsync(wording.Title,
            $"A generation is running on the Engine. {wording.Consequence}",
            wording.Confirm, "Keep running");
        if (choice != 0) return false;

        await _engine.StopAsync();
        return true;
    }
}
