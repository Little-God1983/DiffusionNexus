using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Classes
{
    public class SettingsService : ISettingsService
    {
        private readonly string _filePath;
        public SettingsService()
        {
            var folder = AppDataHelper.GetDataFolder();
            _filePath = Path.Combine(folder, "settings.json");
        }

        public async Task<SettingsModel> LoadAsync()
        {
            if (!File.Exists(_filePath))
                return new SettingsModel();

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<SettingsModel>(json) ?? new SettingsModel();
        }

        public async Task SaveAsync(SettingsModel settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json);
        }
    }
}
