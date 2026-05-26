using Avalonia.Controls;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class DiffusionNexusMainWindow : Window
{
    public DiffusionNexusMainWindow()
    {
        InitializeComponent();
        try
        {
            Icon = SafeAssetBitmap.LoadWindowIcon("avares://DiffusionNexus.UI/Assets/AIKnowledgeIcon.png");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to set main window icon — continuing without it");
        }
        Closing += OnWindowClosing;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not DiffusionNexusMainWindowViewModel viewModel)
        {
            return;
        }

        // Compose a single confirmation that covers both backup and download
        // in-flight cases. We could fire two separate dialogs but stacking them
        // is poor UX — one consolidated message lists everything at risk and
        // asks once.
        var hasBackup = viewModel.IsBackupInProgress;
        var hasDownload = viewModel.IsDownloadInProgress;
        if (!hasBackup && !hasDownload)
        {
            return;
        }

        e.Cancel = true;

        var lines = new List<string>();
        if (hasBackup)
        {
            lines.Add("• A backup is currently in progress and will be aborted.");
        }
        if (hasDownload)
        {
            var name = string.IsNullOrWhiteSpace(viewModel.ActiveDownloadName)
                ? "A model download"
                : $"'{viewModel.ActiveDownloadName}'";
            lines.Add($"• {name} is currently downloading — the partial file will be discarded.");
        }
        lines.Add(string.Empty);
        lines.Add("Are you sure you want to close?");

        var title = (hasBackup, hasDownload) switch
        {
            (true, true) => "Backup and Download In Progress",
            (true, false) => "Backup In Progress",
            _ => "Download In Progress"
        };

        var result = await MessageBox.Show(
            this,
            string.Join("\n", lines),
            title,
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
