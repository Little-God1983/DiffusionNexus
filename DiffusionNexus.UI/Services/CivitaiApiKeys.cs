using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// The one place a Civitai API-key provider is obtained from.
/// </summary>
/// <remarks>
/// Five verbatim copies of <c>GetApiKeyAsync</c> collapsed into
/// <see cref="ICivitaiApiKeyProvider"/> — and then reappeared as five verbatim copies of the same
/// lazy <c>new CivitaiApiKeyProvider(...)</c> fallback, so the next change to those rules (a cache,
/// a timeout) still had five sites to find. This is that fallback, once.
/// <para>
/// It also prefers the singleton <c>App.axaml.cs</c> already registers, which the hand-rolled
/// copies never consulted: a DI-hosted caller now shares one provider instead of quietly
/// constructing a private second one. Constructing remains the fallback for hand-built consumers
/// (tests, design-time) that have no locator.
/// </para>
/// </remarks>
internal static class CivitaiApiKeys
{
    /// <param name="scopeFactory">
    /// Scope factory for the constructed fallback; null asks the locator for one.
    /// </param>
    /// <param name="fallbackSettings">
    /// Settings instance the constructed fallback uses when no scope factory is available at all.
    /// </param>
    internal static ICivitaiApiKeyProvider Resolve(
        IServiceScopeFactory? scopeFactory = null, IAppSettingsService? fallbackSettings = null)
        => App.Services?.GetService<ICivitaiApiKeyProvider>()
           ?? new CivitaiApiKeyProvider(
               scopeFactory ?? App.Services?.GetService<IServiceScopeFactory>(), fallbackSettings);
}
