namespace DiffusionNexus.Domain.Services;

/// <summary>
/// The one Civitai API-key lookup. Five verbatim copies existed (spec §1 RC5); each opened a
/// fresh DI scope because a long-lived IAppSettingsService can hold a stale cached AppSettings
/// entity loaded before the key was saved — that rationale moves here with the code.
/// </summary>
public interface ICivitaiApiKeyProvider
{
    Task<string?> GetApiKeyAsync(CancellationToken ct = default);
}
