using DiffusionNexus.Domain.Services.UnifiedLogging;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// A running ComfyUI reads <c>extra_model_paths.yaml</c> only at start-up, so every caller of
/// <see cref="EngineModelPathsSynchronizer.SyncAsync"/> that rewrote the file (<c>Written</c>)
/// must ask the Engine to restart. The restart waits until no job runs; a request made while the
/// Engine is not running is used up by the next cold start.
/// </summary>
public static class EngineModelFoldersChanged
{
    public const string Message = "Model folders changed; the Diffusion Nexus Engine restarts on its next use.";

    /// <summary>Requests the restart and logs it once, when <paramref name="sync"/> rewrote the file.</summary>
    public static void RequestRestartIfWritten(
        EngineModelPathsSyncResult sync, IManagedComfyUiEngine? engine, IUnifiedLogger? unifiedLogger)
    {
        if (!sync.Written || engine is null)
            return;

        Log.ForContext(typeof(EngineModelFoldersChanged)).Information(Message);
        unifiedLogger?.Info(LogCategory.InstanceManagement, "Diffusion Nexus Engine", Message);
        engine.RequestRestart();
    }
}
