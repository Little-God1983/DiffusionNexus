using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.StableDiffusionCpp;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Resolves the local-diffusion models root from the user's installed ComfyUI package
/// and lazily constructs the singleton <see cref="IDiffusionBackend"/> that the
/// Diffusion Canvas binds to.
///
/// The first call performs DB I/O + native library probing (~10–50 ms but no model load yet);
/// subsequent calls return the cached instance. The actual model load happens on the first
/// <see cref="IDiffusionBackend.GenerateAsync"/> call (per design: load-once-keep-forever).
/// </summary>
public sealed class LocalDiffusionBackendProvider : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<LocalDiffusionBackendProvider>();
    private readonly IServiceProvider _serviceProvider;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private StableDiffusionCppBackend? _backend;
    private string? _resolvedRoot;

    public LocalDiffusionBackendProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// Returns the configured backend, or <c>null</c> if no ComfyUI installation is registered
    /// or its <c>models/</c> folder does not exist. Callers should surface a friendly message
    /// to the user in the null case rather than retrying.
    /// </summary>
    public async Task<IDiffusionBackend?> TryGetAsync(CancellationToken cancellationToken = default)
    {
        if (_backend is not null) return _backend;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backend is not null) return _backend;

            var modelsRoot = await ResolveModelsRootAsync(cancellationToken).ConfigureAwait(false);
            if (modelsRoot is null) return null;

            Logger.Information("Initializing local diffusion backend with models root: {Root}", modelsRoot);
            _backend = new StableDiffusionCppBackend(modelsRoot);
            _resolvedRoot = modelsRoot;
            return _backend;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>The models root the backend was initialized with, or null if not yet initialized.</summary>
    public string? ResolvedModelsRoot => _resolvedRoot;

    private async Task<string?> ResolveModelsRootAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IInstallerPackageRepository>();
            var packages = await repo.GetAllAsync(ct).ConfigureAwait(false);

            // Prefer the default ComfyUI installation; fall back to any ComfyUI installation.
            var comfy = packages.FirstOrDefault(p => p.Type == InstallerType.ComfyUI && p.IsDefault)
                     ?? packages.FirstOrDefault(p => p.Type == InstallerType.ComfyUI);

            if (comfy is null)
            {
                Logger.Warning("No ComfyUI installation found — local diffusion backend cannot start.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(comfy.InstallationPath) || !Directory.Exists(comfy.InstallationPath))
            {
                Logger.Warning("ComfyUI '{Name}' has no valid InstallationPath.", comfy.Name);
                return null;
            }

            var modelsRoot = Path.Combine(comfy.InstallationPath, "models");
            if (!Directory.Exists(modelsRoot))
            {
                Logger.Warning("ComfyUI models folder not found at {Path}.", modelsRoot);
                return null;
            }

            return modelsRoot;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to resolve ComfyUI models root for local diffusion backend.");
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _backend?.Dispose();
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
