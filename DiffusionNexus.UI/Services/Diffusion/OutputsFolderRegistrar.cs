using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Startup hook that ensures the local-diffusion outputs folder
/// (<c>&lt;exe-dir&gt;/outputs/</c>) is registered as an enabled entry in
/// <c>AppSettings.ImageGalleries</c> so the Generation Gallery picks it up automatically.
///
/// Called once during app startup. Safe to call multiple times — entries are matched by path.
/// </summary>
public sealed class OutputsFolderRegistrar
{
    private static readonly ILogger Logger = Log.ForContext<OutputsFolderRegistrar>();
    private readonly IAppSettingsService _settingsService;

    /// <summary>
    /// The absolute path of the outputs folder (next to the running exe). Created on
    /// first access if missing.
    /// </summary>
    public static string OutputsDirectory { get; } = ResolveOutputsDirectory();

    public OutputsFolderRegistrar(IAppSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <summary>
    /// Ensures the outputs folder exists on disk and is registered + enabled in
    /// <see cref="AppSettings.ImageGalleries"/>. No-op if already present.
    /// </summary>
    public async Task EnsureRegisteredAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(OutputsDirectory);

            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            settings.ImageGalleries ??= [];

            if (settings.ImageGalleries.Any(g =>
                string.Equals(g.FolderPath, OutputsDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var nextOrder = settings.ImageGalleries.Count == 0
                ? 0
                : settings.ImageGalleries.Max(g => g.Order) + 1;

            settings.ImageGalleries.Add(new ImageGallery
            {
                FolderPath = OutputsDirectory,
                IsEnabled = true,
                Order = nextOrder,
            });

            await _settingsService.SaveSettingsAsync(settings).ConfigureAwait(false);
            Logger.Information("Registered Diffusion Canvas outputs folder in Generation Gallery: {Path}", OutputsDirectory);
        }
        catch (Exception ex)
        {
            // Non-fatal: gallery registration failure must not block app startup.
            Logger.Warning(ex, "Failed to register Diffusion Canvas outputs folder in Generation Gallery.");
        }
    }

    private static string ResolveOutputsDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        return Path.Combine(exeDir, "outputs");
    }
}
