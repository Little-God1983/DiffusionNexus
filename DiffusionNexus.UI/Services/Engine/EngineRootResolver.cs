using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>Finds the Diffusion Nexus Engine's install folder.</summary>
public interface IEngineRootResolver
{
    /// <summary>
    /// Returns the app-managed ComfyUI install path, or null when the Engine was never installed.
    /// Rewrites the Engine's extra_model_paths.yaml first, so a model folder added in Settings is
    /// visible to the check or start that follows.
    /// </summary>
    Task<string?> ResolveAsync(CancellationToken ct = default);
}

/// <summary>
/// Moved verbatim from the lambda that used to live in the ManagedComfyUiBackend registration in
/// App.axaml.cs, so the client provider and the Engine readiness backend resolve the root the same way.
/// </summary>
public sealed class EngineRootResolver : IEngineRootResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Func<string, CancellationToken, Task<bool>>? _syncModelPaths;
    private readonly Action? _onModelPathsChanged;
    private readonly IUnifiedLogger? _unifiedLogger;

    /// <param name="scopes">Creates the scope that owns the IUnitOfWork for one resolve.</param>
    /// <param name="syncModelPaths">
    /// Test seam; returns true when the file changed. When null, <see cref="EngineModelPathsSynchronizer"/>
    /// is resolved from the same scope.
    /// </param>
    /// <param name="onModelPathsChanged">
    /// Called when the sync rewrote extra_model_paths.yaml. A running Engine reads that file only at
    /// start-up, so DI asks the Engine to restart here.
    /// </param>
    public EngineRootResolver(
        IServiceScopeFactory scopes,
        Func<string, CancellationToken, Task<bool>>? syncModelPaths = null,
        Action? onModelPathsChanged = null,
        IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
        _syncModelPaths = syncModelPaths;
        _onModelPathsChanged = onModelPathsChanged;
        _unifiedLogger = unifiedLogger;
    }

    public async Task<string?> ResolveAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var packages = await uow.InstallerPackages.GetAllAsync(ct);
        var installRoot = packages.FirstOrDefault(p => p.IsAppManaged)?.InstallationPath;

        // The engine reads extra_model_paths.yaml once, at process start, and the disk check derives
        // its search paths from the same file. This resolver runs immediately before both, so it is
        // the one point where "the folder list in Settings changed" can still be acted on. Never
        // fails the resolve: the synchronizer swallows its own errors.
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            var changed = _syncModelPaths is not null
                ? await _syncModelPaths(installRoot, ct)
                : (await scope.ServiceProvider.GetRequiredService<EngineModelPathsSynchronizer>()
                    .SyncAsync(installRoot, ct)).Written;

            // A running Engine read the old file at start-up and cannot see the new folders until it
            // restarts. The restart waits until no job is running on the Engine.
            if (changed && _onModelPathsChanged is not null)
            {
                const string message = "Model folders changed; the Diffusion Nexus Engine restarts on its next use.";
                Log.ForContext<EngineRootResolver>().Information(message);
                _unifiedLogger?.Info(LogCategory.InstanceManagement, "Diffusion Nexus Engine", message);
                _onModelPathsChanged();
            }
        }

        return string.IsNullOrWhiteSpace(installRoot) ? null : installRoot;
    }
}
