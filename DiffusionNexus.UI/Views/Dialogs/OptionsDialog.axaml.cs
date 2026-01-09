using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System.Collections.ObjectModel;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// A dialog window that presents multiple options as buttons.
/// </summary>
public partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets or sets the message to display.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets the option items for binding.
    /// </summary>
    public ObservableCollection<OptionItem> OptionItems { get; } = [];

    /// <summary>
    /// Gets the selected option index after the dialog closes.
    /// -1 if cancelled (closed without selection).
    /// </summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>
    /// Sets the available options.
    /// </summary>
    /// <param name="options">Array of option labels.</param>
    public void SetOptions(params string[] options)
    {
        OptionItems.Clear();
        for (var i = 0; i < options.Length; i++)
        {
            IBrush background;
            if (i == 0)
            {
                // Cancel is first, neutral
                background = new SolidColorBrush(Color.Parse("#333"));
            }
            else if (i == options.Length - 1)
            {
                // Last option is primary (green)
                background = new SolidColorBrush(Color.Parse("#2D7D46"));
            }
            else
            {
                // Middle options
                background = new SolidColorBrush(Color.Parse("#444"));
            }

            OptionItems.Add(new OptionItem
            {
                Index = i,
                Label = options[i],
                Background = background
            });
        }
    }

    private void OnOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int index)
        {
            SelectedIndex = index;
            Close(index);
        }
    }
}

/// <summary>
/// Represents a single option in the dialog.
/// </summary>
public class OptionItem
{
    public int Index { get; set; }
    public string Label { get; set; } = string.Empty;
    public IBrush? Background { get; set; }
}
