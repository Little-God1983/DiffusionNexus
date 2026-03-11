using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

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
    private static async Task<(bool IsValid, string? ErrorMessage)> ValidateTokenAsync(string token)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://civitai.com/api/v1/"),
                Timeout = TimeSpan.FromSeconds(15)
            };
            httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            using var request = new HttpRequestMessage(HttpMethod.Get, "models?limit=1");
            request.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {token}");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (response.IsSuccessStatusCode)
                return (true, null);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                return (false, "Invalid API token. The token was rejected by Civitai (401/403).");
            }

            return (false, $"Civitai returned an unexpected status: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Connection timed out. Please check your internet connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Could not reach Civitai: {ex.Message}");
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
