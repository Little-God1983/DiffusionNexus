using Avalonia.Controls.ApplicationLifetimes;
using DiffusionNexus.Installer.SDK.Shared.Services;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Production <see cref="IClipboardService"/> — resolves the system clipboard
/// from the desktop main window's <c>TopLevel</c> and delegates to it. Stateless,
/// so a single shared <see cref="Instance"/> is used as the default for ViewModel
/// constructors and the same type is registered as a DI singleton.
/// <para>
/// Reuses the shared <see cref="IClipboardService"/> contract from the Installer
/// SDK (identical signature) rather than duplicating it in the UI project.
/// </para>
/// </summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    /// <summary>
    /// Shared instance used as the default when no clipboard service is injected
    /// (keeps existing construction sites behaving identically).
    /// </summary>
    public static AvaloniaClipboardService Instance { get; } = new();

    /// <inheritdoc/>
    // TODO: Linux Implementation for clipboard access via a non-desktop TopLevel.
    public async Task SetTextAsync(string text)
    {
        var topLevel = Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        var clipboard = topLevel?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
