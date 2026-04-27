using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.StableDiffusionCpp;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Resolves the local-diffusion models roots from <b>every</b> ComfyUI installation registered
/// in the Installer Manager and lazily constructs the singleton <see cref="IDiffusionBackend"/>
/// that the Diffusion Canvas binds to.
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
    private IReadOnlyList<string> _resolvedRoots = [];

    public LocalDiffusionBackendProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// Returns the configured backend, or <c>null</c> if no usable ComfyUI installation is
    /// registered. Callers should surface a friendly message to the user in the null case
    /// rather than retrying.
    /// </summary>
    public async Task<IDiffusionBackend?> TryGetAsync(CancellationToken cancellationToken = default)
    {
        if (_backend is not null) return _backend;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backend is not null) return _backend;

            var roots = await ResolveModelsRootsAsync(cancellationToken).ConfigureAwait(false);
            if (roots.Count == 0) return null;

            Logger.Information("Initializing local diffusion backend with {Count} models root(s): {Roots}",
                roots.Count, string.Join(" | ", roots));
            _backend = new StableDiffusionCppBackend(roots);
            _resolvedRoots = roots;
            return _backend;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// The first models root resolved (for legacy single-root callers / status messages).
    /// Returns null if the backend has not been initialized.
    /// </summary>
    public string? ResolvedModelsRoot => _resolvedRoots.Count > 0 ? _resolvedRoots[0] : null;

    /// <summary>All models roots (one per discovered ComfyUI installation) the backend will search.</summary>
    public IReadOnlyList<string> ResolvedModelsRoots => _resolvedRoots;

    private async Task<IReadOnlyList<string>> ResolveModelsRootsAsync(CancellationToken ct)
    {
        try
        {
            // Use IUnitOfWork (same path as InstallerManagerViewModel). Repositories are NOT
            // registered directly in DI — they are only accessible through the Unit of Work.
            using var scope = _serviceProvider.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var packages = await uow.InstallerPackages.GetAllAsync(ct).ConfigureAwait(false);

            Logger.Information("LocalDiffusionBackendProvider: Found {Count} total packages in database.", packages.Count);

            // Take every ComfyUI installation; default(s) first so they win on duplicate filenames.
            var comfyInstalls = packages
                .Where(p => p.Type == InstallerType.ComfyUI)
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Name)
                .ToList();

            if (comfyInstalls.Count == 0)
            {
                var typeCounts = packages.GroupBy(p => p.Type).Select(g => $"{g.Key}={g.Count()}").ToList();
                Logger.Warning(
                    "No ComfyUI installation found in database (looked for InstallerType.ComfyUI). " +
                    "Found: [{Types}]. The local diffusion backend uses the ComfyUI models folder layout " +
                    "but does NOT run ComfyUI — it generates locally on your GPU.",
                    string.Join(", ", typeCounts));
                return [];
            }

            var roots = new List<string>(comfyInstalls.Count);
            foreach (var pkg in comfyInstalls)
            {
                var root = ResolveSingleRoot(pkg);
                if (root is not null && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(root);
                    Logger.Information("ComfyUI installation '{Name}' → models root: {Root}", pkg.Name, root);
                }
            }

            if (roots.Count == 0)
            {
                Logger.Warning("Found {Count} ComfyUI installation(s) but none had a usable models folder.", comfyInstalls.Count);
            }

            return roots;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to resolve ComfyUI models roots for local diffusion backend.");
            return [];
        }
    }

    /// <summary>
    /// Resolves a single ComfyUI installation's models folder, accounting for the same
    /// portable-vs-manual layouts the Installer's ConfigurationCheckerService recognizes.
    /// </summary>
    private static string? ResolveSingleRoot(InstallerPackage pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg.InstallationPath) || !Directory.Exists(pkg.InstallationPath))
        {
            Logger.Warning("ComfyUI '{Name}' (ID={Id}) has no valid InstallationPath: '{Path}'.",
                pkg.Name, pkg.Id, pkg.InstallationPath);
            return null;
        }

        // Probe both layouts:
        //   Manual:   <root>/models/
        //   Portable: <root>/ComfyUI/models/  (some installers also keep <root>/models/ at the parent level)
        var candidates = new[]
        {
            Path.Combine(pkg.InstallationPath, "models"),
            Path.Combine(pkg.InstallationPath, "ComfyUI", "models"),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        Logger.Warning("ComfyUI '{Name}' has no models folder under {Root}. Probed: [{Candidates}].",
            pkg.Name, pkg.InstallationPath, string.Join(", ", candidates));
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _backend?.Dispose();
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
