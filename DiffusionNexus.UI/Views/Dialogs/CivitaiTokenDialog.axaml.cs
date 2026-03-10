using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog that prompts the user for a Civitai API token when one is not configured.
/// Shows instructions, a reference image, and an input field.
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
    /// True if the user clicked Save, false if cancelled.
    /// </summary>
    public bool IsSaved { get; private set; }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TokenText))
            return;

        IsSaved = true;
        Close(true);
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
}
