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
            if (!result.IsRunning)
                _logger.Warn(LogCategory.InstanceManagement, Source,
                    $"The Engine did not start: {result.FailureReason ?? "no reason was given"}");
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.InstanceManagement, Source, $"Could not start the Engine: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Stops the Engine. While a generation runs on it, asks first: stopping cancels that generation.
    /// Returns whether it stopped.
    /// </summary>
    public async Task<bool> StopAsync()
    {
        try
        {
            if (!await ConfirmStopAsync("Stop the Engine?")) return false;
            await _engine.StopAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.InstanceManagement, Source, $"Could not stop the Engine: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>Stops the Engine and starts it again, asking first while a generation runs.</summary>
    public async Task RestartAsync()
    {
        try
        {
            if (!await ConfirmStopAsync("Restart the Engine?")) return;
            await _engine.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.InstanceManagement, Source, $"Could not restart the Engine: {ex.Message}", ex);
            return;
        }

        await StartAsync();
    }

    // With no generation running there is nothing to lose: no question. With one, the user decides;
    // a dismissed dialog keeps it running. Without a dialog to ask with, it keeps running and says why.
    private async Task<bool> ConfirmStopAsync(string title)
    {
        if (!_engine.HasActiveJobs) return true;
        if (_dialogs is null)
        {
            _logger.Warn(LogCategory.InstanceManagement, Source,
                "A generation is running on the Engine, so it was not stopped. Stop it when the generation has finished.");
            return false;
        }

        var choice = await _dialogs.ShowOptionsAsync(title,
            "A generation is running on the Engine. Stopping it cancels that generation.",
            "Stop anyway", "Keep running");
        return choice == 0;
    }
}
