using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// A simple text input dialog window.
/// </summary>
public partial class TextInputDialog : Window
{
    public TextInputDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets or sets the prompt message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the input text.
    /// </summary>
    public string InputText { get; set; } = string.Empty;

    /// <summary>
    /// Gets the result text after the dialog closes.
    /// Null if cancelled.
    /// </summary>
    public string? ResultText { get; private set; }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        ResultText = InputText;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ResultText = null;
        Close(false);
    }
}
