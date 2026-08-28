using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Civitai;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog that prompts the user for a Civitai API token when one is not configured.
/// Validates the token against the Civitai API before accepting it.
/// </summary>
public partial class CivitaiTokenDialog : Window
{
    /// <summary>
    /// Styled property for the token text binding.
    /// </summary>
    public static readonly StyledProperty<string> TokenTextProperty =
        AvaloniaProperty.Register<CivitaiTokenDialog, string>(nameof(TokenText), defaultValue: string.Empty);

    public CivitaiTokenDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets or sets the API token entered by the user.
    /// </summary>
    public string TokenText
    {
        get => GetValue(TokenTextProperty);
        set => SetValue(TokenTextProperty, value);
    }

    /// <summary>
    /// True if the user clicked Save and the token was validated, false if cancelled.
    /// </summary>
    public bool IsSaved { get; private set; }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TokenText))
            return;

        var token = TokenText.Trim();

        // Show validation UI
        SetValidating(true, "Validating token with Civitai...");

        var (isValid, errorMessage) = await ValidateTokenAsync(token);

        SetValidating(false);

        if (isValid)
        {
            IsSaved = true;
            Close(true);
        }
        else
        {
            ShowError(errorMessage ?? "The API token is not valid. Please check and try again.");
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        IsSaved = false;
        Close(false);
    }

    // TODO: Linux Implementation for opening browser
    private void OnCivitaiLinkClick(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://civitai.com/user/account",
                UseShellExecute = true
            });
        }
        catch
        {
            // Silently fail if browser cannot be opened
        }
    }

    /// <summary>
    /// Validates the token by making a lightweight authenticated request to Civitai.
    /// Uses <c>GET /api/v1/models?limit=1</c> which returns 401 for invalid tokens.
    /// </summary>
    /// <remarks>
    /// Routed through the shared <see cref="ICivitaiClient"/> gateway (the interactive lane) rather
    /// than a private <c>HttpClient</c> — this was the one call site left hammering Civitai
    /// unpaced and invisible to the shared 429 cooldown after <c>CivitaiApiGateway</c> became the
    /// one door everywhere else. The just-typed <paramref name="token"/> is passed as the call's
    /// <c>apiKey</c> argument, NOT read from whatever key is currently configured/saved — this
    /// dialog exists specifically to test a token before it is saved.
    /// </remarks>
    private static async Task<(bool IsValid, string? ErrorMessage)> ValidateTokenAsync(string token)
    {
        var client = App.Services?.GetService<ICivitaiClient>();
        if (client is null)
            return (false, "Could not reach Civitai: the API client is not available.");

        // The gateway's cache never keys by apiKey (an authenticated and an anonymous request for
        // the same public page return the same answer), so a "limit=1" search cached moments ago
        // under a different key — or under this same token from a previous click — could otherwise
        // hand back a stale answer instead of actually asking Civitai about THIS token right now.
        // Clearing first guarantees this call is a real round-trip. It also empties the shared
        // cache for every other surface (browser, detail panel, sync), same as a normal saved-key
        // change already does via CivitaiResponseCache.NoteApiKey — acceptable here because
        // validating a token is a deliberate, infrequent, user-initiated action (typically while
        // the cache is still empty, during onboarding), not something that happens mid-scroll.
        if (client is ICivitaiApiCache cache) cache.Clear();

        try
        {
            await client.GetModelsAsync(new CivitaiModelsQuery { Limit = 1 }, apiKey: token);
            return (true, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (false, "Invalid API token. The token was rejected by Civitai (401/403).");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            return (false, $"Civitai returned an unexpected status: {(int)status} {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Connection timed out. Please check your internet connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Could not reach Civitai: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Routing through the gateway means the response is now actually deserialized
            // (CivitaiClient.DeserializeOrThrow can raise JsonException on a shape it doesn't
            // recognize) — the removed raw client never parsed a body, so this path is new. This
            // method is awaited from an `async void` event handler (OnSaveClick): anything that
            // escapes it terminates the process rather than just failing the validation, so this
            // must be a true catch-all rather than one more specific exception type.
            return (false, $"Unexpected error validating the token: {ex.Message}");
        }
    }

    private void SetValidating(bool isValidating, string? message = null)
    {
        var panel = this.FindControl<StackPanel>("ValidationPanel");
        var msgBlock = this.FindControl<TextBlock>("ValidationMessage");
        var saveBtn = this.FindControl<Button>("SaveButton");
        var cancelBtn = this.FindControl<Button>("CancelButton");
        var tokenBox = this.FindControl<TextBox>("TokenTextBox");
        var errorBlock = this.FindControl<TextBlock>("ErrorMessage");

        if (panel is not null) panel.IsVisible = isValidating;
        if (msgBlock is not null) msgBlock.Text = message;
        if (saveBtn is not null) saveBtn.IsEnabled = !isValidating;
        if (cancelBtn is not null) cancelBtn.IsEnabled = !isValidating;
        if (tokenBox is not null) tokenBox.IsEnabled = !isValidating;
        if (errorBlock is not null) errorBlock.IsVisible = false;
    }

    private void ShowError(string message)
    {
        var errorBlock = this.FindControl<TextBlock>("ErrorMessage");
        if (errorBlock is not null)
        {
            errorBlock.Text = message;
            errorBlock.IsVisible = true;
        }
    }
}

/// <summary>
/// Result of the <see cref="CivitaiTokenDialog"/> shown via
/// <see cref="Services.IDialogService.ShowCivitaiTokenDialogAsync"/>.
/// </summary>
/// <param name="IsSaved">True when the user saved a validated token.</param>
/// <param name="TokenText">The entered token text (empty when cancelled).</param>
public readonly record struct CivitaiTokenDialogResult(bool IsSaved, string TokenText);
