using System;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Classes;

public class SettingsUserSettings : IUserSettings
{
    private readonly ISettingsService _settingsService;

    public SettingsUserSettings(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public async Task<string?> GetLastDownloadLoraTargetAsync()
    {
        var settings = await _settingsService.LoadAsync();
        return settings.LastDownloadLoraTarget;
    }

    public async Task SetLastDownloadLoraTargetAsync(string? path)
    {
        var settings = await _settingsService.LoadAsync();
        settings.LastDownloadLoraTarget = path;
        await _settingsService.SaveAsync(settings);
    }
}
