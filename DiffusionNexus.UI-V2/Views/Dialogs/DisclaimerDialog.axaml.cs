using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Disclaimer dialog that must be accepted before using the application.
/// </summary>
public partial class DisclaimerDialog : Window
{
    public DisclaimerDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets or sets whether the user has accepted the disclaimer.
    /// </summary>
    public bool IsAccepted { get; set; }

    /// <summary>
    /// Gets the result after the dialog closes.
    /// True if accepted, false if cancelled.
    /// </summary>
    public bool Result { get; private set; }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        if (IsAccepted)
        {
            Result = true;
            Close(true);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // If closing without accepting, ensure Result is false
        if (!Result)
        {
            Result = false;
        }
        base.OnClosing(e);
    }
}
