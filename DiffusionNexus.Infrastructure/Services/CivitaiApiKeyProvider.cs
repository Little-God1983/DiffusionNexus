using DiffusionNexus.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Infrastructure.Services;

/// <summary>
/// Default <see cref="ICivitaiApiKeyProvider"/>: resolves a fresh <see cref="IAppSettingsService"/>
/// from a new DI scope on every call — see the interface doc comment for why a long-lived instance
/// is not reused. Falls back to <paramref name="fallbackSettings"/> when no scope factory is
/// available (hand-constructed consumers without DI, e.g. design-time/tests), and to
/// <c>null</c> when neither is available. Never throws.
/// </summary>
public sealed class CivitaiApiKeyProvider : ICivitaiApiKeyProvider
{
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IAppSettingsService? _fallbackSettings;

    public CivitaiApiKeyProvider(IServiceScopeFactory? scopeFactory, IAppSettingsService? fallbackSettings = null)
    {
        _scopeFactory = scopeFactory;
        _fallbackSettings = fallbackSettings;
    }

    public async Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        if (_scopeFactory is not null)
        {
            using var scope = _scopeFactory.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            return await settingsService.GetCivitaiApiKeyAsync(ct);
        }

        return _fallbackSettings is not null
            ? await _fallbackSettings.GetCivitaiApiKeyAsync(ct)
            : null;
    }
}
