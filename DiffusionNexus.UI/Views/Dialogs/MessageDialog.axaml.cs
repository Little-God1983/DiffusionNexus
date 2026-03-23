using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// A simple informational message dialog with a single OK button.
/// </summary>
public partial class MessageDialog : Window
{
    /// <summary>
    /// Styled property so the binding updates when Message is set after construction.
    /// </summary>
    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<MessageDialog, string>(nameof(Message), defaultValue: string.Empty);

    public MessageDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets or sets the informational message.
    /// </summary>
    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
