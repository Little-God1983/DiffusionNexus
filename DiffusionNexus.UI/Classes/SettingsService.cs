using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DiffusionNexus.DataAccess.Interfaces;
using DiffusionNexus.DataAccess.Infrastructure;
using DiffusionNexus.DataAccess.Infrastructure.Serialization;

namespace DiffusionNexus.UI.Classes
{
    public class SettingsService : ISettingsService
    {
        private readonly IConfigStore _store;

        public SettingsService() : this(new FileConfigStore(AppDataHelper.GetDataFolder(), new JsonSerializerAdapter()))
        {
        }

        public SettingsService(IConfigStore store)
        {
            _store = store;
        }

        public async Task<SettingsModel> LoadAsync()
        {
            var model = _store.Load<SettingsModel>("settings");
            // Decrypt API key after loading
            model.CivitaiApiKey = SecureStorageHelper.DecryptString(model.EncryptedCivitaiApiKey);
            return model;
        }

        public async Task SaveAsync(SettingsModel settings)
        {
            // Encrypt API key before saving
            settings.EncryptedCivitaiApiKey = string.IsNullOrWhiteSpace(settings.CivitaiApiKey)
                ? null
                : SecureStorageHelper.EncryptString(settings.CivitaiApiKey);
            _store.Save("settings", settings);
            await Task.CompletedTask;
        }
    }
}
