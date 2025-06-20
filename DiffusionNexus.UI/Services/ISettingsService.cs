using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Services
{
    public interface ISettingsService
    {
        AppSettings Load();
        void Save(AppSettings settings);
    }
}
