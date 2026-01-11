using Avalonia.Controls;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class DiffusionNexusMainWindow : Window
{
    public DiffusionNexusMainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not DiffusionNexusMainWindowViewModel viewModel)
        {
            return;
        }

        // Check if backup is in progress
        if (!viewModel.IsBackupInProgress)
        {
            return;
        }

        // Cancel the close and ask user
        e.Cancel = true;

        var result = await MessageBox.Show(
            this,
            "A backup is currently in progress. If you close now, the backup will be aborted.\n\nAre you sure you want to close?",
            "Backup In Progress",
            MessageBox.MessageBoxButtons.YesNo,
            MessageBox.MessageBoxIcon.Warning);

        if (result == MessageBox.MessageBoxResult.Yes)
        {
            // User confirmed - close without asking again
            Closing -= OnWindowClosing;
            Close();
        }
    }
}

/// <summary>
/// Simple message box helper for Avalonia.
/// </summary>
public static class MessageBox
{
    public enum MessageBoxButtons
    {
        Ok,
        OkCancel,
        YesNo,
        YesNoCancel
    }

    public enum MessageBoxIcon
    {
        None,
        Information,
        Warning,
        Error,
        Question
    }

    public enum MessageBoxResult
    {
        None,
        Ok,
        Cancel,
        Yes,
        No
    }

    public static async Task<MessageBoxResult> Show(
        Window parent,
        string message,
        string title,
        MessageBoxButtons buttons = MessageBoxButtons.Ok,
        MessageBoxIcon icon = MessageBoxIcon.None)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var result = MessageBoxResult.None;

        var iconText = icon switch
        {
            MessageBoxIcon.Warning => "??",
            MessageBoxIcon.Error => "?",
            MessageBoxIcon.Information => "??",
            MessageBoxIcon.Question => "?",
            _ => ""
        };

        var mainPanel = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16
        };

        var messagePanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 12
        };

        if (!string.IsNullOrEmpty(iconText))
        {
            messagePanel.Children.Add(new TextBlock
            {
                Text = iconText,
                FontSize = 24,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            });
        }

        messagePanel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 320,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });

        mainPanel.Children.Add(messagePanel);

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };

        void AddButton(string text, MessageBoxResult buttonResult, bool isDefault = false)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 80,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            
            if (isDefault)
            {
                button.Classes.Add("accent");
            }

            button.Click += (_, _) =>
            {
                result = buttonResult;
                dialog.Close();
            };
            
            buttonPanel.Children.Add(button);
        }

        switch (buttons)
        {
            case MessageBoxButtons.Ok:
                AddButton("OK", MessageBoxResult.Ok, true);
                break;
            case MessageBoxButtons.OkCancel:
                AddButton("OK", MessageBoxResult.Ok, true);
                AddButton("Cancel", MessageBoxResult.Cancel);
                break;
            case MessageBoxButtons.YesNo:
                AddButton("Yes", MessageBoxResult.Yes);
                AddButton("No", MessageBoxResult.No, true);
                break;
            case MessageBoxButtons.YesNoCancel:
                AddButton("Yes", MessageBoxResult.Yes);
                AddButton("No", MessageBoxResult.No);
                AddButton("Cancel", MessageBoxResult.Cancel, true);
                break;
        }

        mainPanel.Children.Add(buttonPanel);
        dialog.Content = mainPanel;

        await dialog.ShowDialog(parent);
        return result;
    }
}
