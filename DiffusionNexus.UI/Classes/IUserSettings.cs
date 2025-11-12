using System.Threading.Tasks;

namespace DiffusionNexus.UI.Classes;

public interface IUserSettings
{
    Task<string?> GetLastDownloadLoraTargetAsync();
    Task SetLastDownloadLoraTargetAsync(string? path);
}
