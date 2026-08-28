using DiffusionNexus.Civitai;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Registers the one door to the Civitai API. Pulled out of <c>App.axaml.cs</c> so the sharing
/// invariant it exists to establish — one pacer, one cooldown, one cache behind both lanes — is
/// something a test can build a <see cref="ServiceProvider"/> from and assert directly, rather
/// than something only visible by reading the DI block in the app's startup file.
/// </summary>
/// <remarks>
/// Deliberately not in <c>DiffusionNexus.Civitai</c>: that project carries zero project references
/// and zero NuGet packages by design, and an <see cref="IServiceCollection"/> extension would drag
/// in <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>.
/// </remarks>
public static class CivitaiGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared pacer, cooldown and cache, the raw <see cref="CivitaiClient"/> (wired
    /// to report every 429 to the shared cooldown), and both gateway lanes: the default
    /// <see cref="ICivitaiClient"/> (interactive — a user is waiting) and the keyed
    /// <c>"background"</c> <see cref="ICivitaiClient"/> (nobody is waiting — library sync, the
    /// visible-tile update sweep).
    /// </summary>
    /// <remarks>
    /// Everything here is a singleton because the pacing timestamp, the 429 cooldown and the cache
    /// are the process's single opinion about Civitai — a second copy of any of them would pace,
    /// cool down and cache nothing. Both lanes resolve the same three collaborators through
    /// <c>sp.GetRequiredService</c>; that shared resolution, not anything special about the
    /// gateway class itself, is what makes a 429 drawn in one lane pause the other.
    /// </remarks>
    public static IServiceCollection AddCivitaiGateway(this IServiceCollection services)
    {
        services.AddSingleton<CivitaiRateLimitCooldown>();
        services.AddSingleton<CivitaiResponseCache>();
        services.AddSingleton<ICivitaiApiCache>(sp => sp.GetRequiredService<CivitaiResponseCache>());
        services.AddSingleton<ICivitaiRequestPacer>(_ => new CivitaiRequestPacer());

        // The raw client, told to report every 429 to the shared cooldown the moment it sees one.
        services.AddSingleton(sp => new CivitaiClient(
            new HttpClient(),
            disposeHttpClient: true,
            rateLimitObserver: sp.GetRequiredService<CivitaiRateLimitCooldown>()));

        // Default lane: a user is waiting. Resolved by the browser, the detail panel, the
        // dialogs, the download path, the waitlist, the sorter and the pipeline installer.
        services.AddSingleton<ICivitaiClient>(sp => new CivitaiApiGateway(
            sp.GetRequiredService<CivitaiClient>(),
            sp.GetRequiredService<ICivitaiRequestPacer>(),
            sp.GetRequiredService<CivitaiRateLimitCooldown>(),
            sp.GetRequiredService<CivitaiResponseCache>(),
            CivitaiCallLane.Interactive));

        // Background lane: nobody is waiting. Library sync and the visible-tile update sweep.
        services.AddKeyedSingleton<ICivitaiClient>("background", (sp, _) => new CivitaiApiGateway(
            sp.GetRequiredService<CivitaiClient>(),
            sp.GetRequiredService<ICivitaiRequestPacer>(),
            sp.GetRequiredService<CivitaiRateLimitCooldown>(),
            sp.GetRequiredService<CivitaiResponseCache>(),
            CivitaiCallLane.Background));

        return services;
    }
}
