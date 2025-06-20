using System.Threading.Tasks;

namespace DiffusionNexus.UI.Classes
{
    public interface ISettingsService
    {
        Task<SettingsModel> LoadAsync();
        Task SaveAsync(SettingsModel settings);
    }
}
