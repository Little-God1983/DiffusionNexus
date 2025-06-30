using System.Threading.Tasks;

namespace DiffusionNexus.UI.Classes
{
    public interface IDialogService
    {
        Task<bool?> ShowConfirmationAsync(string message, bool allowCancel = false);
        Task<string?> ShowInputAsync(string message, string? defaultValue = null);
        Task<bool> ShowOverwriteConfirmationAsync();
        Task<bool> ShowYesNoAsync(string message, string title);
    }
}
