using System.Diagnostics;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// The one way the app opens a web URL in the system browser. ViewModels keep a swappable
/// <c>Action&lt;string&gt;</c> opener (default <see cref="Open"/>) so tests can capture the URL,
/// and launch through <see cref="TryOpen"/> so a machine with no default browser, or a shell that
/// refuses the protocol, logs a warning instead of throwing out of a command on the UI thread.
/// </summary>
public static class UrlLauncher
{
    /// <summary>Hands <paramref name="url"/> to the shell. Throws when the shell cannot open it.</summary>
    public static void Open(string url)
    {
        // The browser process is not ours to watch; dispose the handle Process.Start returns.
        using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// Opens <paramref name="url"/> through <paramref name="opener"/>, logging a failure under
    /// <paramref name="source"/> instead of throwing.
    /// </summary>
    /// <returns><c>true</c> when the opener returned normally.</returns>
    public static bool TryOpen(string url, Action<string> opener, IUnifiedLogger? logger, string source)
    {
        try
        {
            opener(url);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warn(LogCategory.General, source, $"Failed to launch browser for {url}: {ex.Message}");
            return false;
        }
    }
}
