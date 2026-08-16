using DiffusionNexus.Installer.SDK.Shared.Services;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Bridges the Installer SDK's prompt contract onto the app's dialog service. Against the
/// fresh, empty engine folder the pre-checks are trivial; the prompt that can genuinely fire
/// is the GPU gate's CPU-only offer, which the user must answer honestly rather than have
/// declined on their behalf.
/// </summary>
public sealed class DialogUserPromptService : IUserPromptService
{
    private readonly IDialogService _dialogService;

    public DialogUserPromptService(IDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        _dialogService = dialogService;
    }

    public Task<bool> ConfirmAsync(string title, string message,
        string yesButtonText = "Yes", string noButtonText = "No")
        => _dialogService.ShowConfirmAsync(title, message);

    public Task ShowErrorAsync(string title, string message)
        => _dialogService.ShowMessageAsync(title, message);

    public Task ShowInfoAsync(string title, string message)
        => _dialogService.ShowMessageAsync(title, message);
}
