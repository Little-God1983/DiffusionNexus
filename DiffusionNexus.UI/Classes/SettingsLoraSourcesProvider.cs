using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Classes;

public class SettingsLoraSourcesProvider : ILoraSourcesProvider
{
    private readonly ISettingsService _settingsService;

    public SettingsLoraSourcesProvider(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public async Task<IReadOnlyList<LoraSourceInfo>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.LoadAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var sources = settings.LoraHelperSources
            .Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.FolderPath))
            .Select(s => s.FolderPath!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(trimmed);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = trimmed;
                }

                return new LoraSourceInfo(name, trimmed);
            })
            .ToList();

        return sources;
    }
}
