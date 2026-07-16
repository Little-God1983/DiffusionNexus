using System;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Installer.SDK.Shared.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Display wrapper around a fetched <see cref="ServerMessage"/> for the main app's banner.
/// Exposes presentation-friendly properties (severity colours, icon, visibility flags) plus the
/// dismiss / open-link commands. Items are recreated on each fetch and removed wholesale on
/// dismiss, so no per-property change notification is required.
/// </summary>
public sealed class ServerMessageItemViewModel
{
    private readonly ServerMessage _message;

    public ServerMessageItemViewModel(
        ServerMessage message,
        Action<ServerMessageItemViewModel> onDismiss,
        Action<string> onOpenAction)
    {
        _message = message;
        DismissCommand = new RelayCommand(() => onDismiss(this));
        OpenActionCommand = new RelayCommand(() =>
        {
            if (HasAction)
            {
                onOpenAction(_message.ActionUrl!);
            }
        });
    }

    public string Id => _message.Id;
    public string? Title => _message.Title;
    public bool HasTitle => !string.IsNullOrWhiteSpace(_message.Title);
    public string Message => _message.Message;
    public bool IsDismissible => _message.Dismissible;
    public bool HasAction => !string.IsNullOrWhiteSpace(_message.ActionUrl);
    public string ActionLabel => string.IsNullOrWhiteSpace(_message.ActionLabel) ? "Learn more" : _message.ActionLabel!;

    public ICommand DismissCommand { get; }
    public ICommand OpenActionCommand { get; }

    /// <summary>Accent colour for the icon, border and action button (severity-driven).</summary>
    public IBrush AccentBrush => new SolidColorBrush(Color.Parse(AccentHex));

    /// <summary>Subtle tinted background for the banner row (severity-driven).</summary>
    public IBrush BackgroundBrush => new SolidColorBrush(Color.Parse(BackgroundHex));

    /// <summary>Leading glyph hinting the severity.</summary>
    public string Icon => _message.Severity switch
    {
        ServerMessageSeverity.Critical => "⚠",
        ServerMessageSeverity.Warning => "⚠",
        _ => "ℹ"
    };

    private string AccentHex => _message.Severity switch
    {
        ServerMessageSeverity.Critical => "#E05A57",
        ServerMessageSeverity.Warning => "#E0A93F",
        _ => "#0078D4"
    };

    private string BackgroundHex => _message.Severity switch
    {
        ServerMessageSeverity.Critical => "#2A1A1F",
        ServerMessageSeverity.Warning => "#2A241A",
        _ => "#1E2A33"
    };
}
