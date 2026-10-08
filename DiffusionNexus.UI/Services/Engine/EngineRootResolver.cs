using DiffusionNexus.DataAccess.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly Func<string, CancellationToken, Task>? _syncModelPaths;

    /// <param name="scopes">Creates the scope that owns the IUnitOfWork for one resolve.</param>
    /// <param name="syncModelPaths">
    /// Test seam. When null, <see cref="EngineModelPathsSynchronizer"/> is resolved from the same scope.
    /// </param>
    public EngineRootResolver(IServiceScopeFactory scopes, Func<string, CancellationToken, Task>? syncModelPaths = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
        _syncModelPaths = syncModelPaths;
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
            if (_syncModelPaths is not null)
                await _syncModelPaths(installRoot, ct);
            else
                await scope.ServiceProvider.GetRequiredService<EngineModelPathsSynchronizer>()
                    .SyncAsync(installRoot, ct);
        }

        return string.IsNullOrWhiteSpace(installRoot) ? null : installRoot;
    }
}
