using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Views.Dialogs;

namespace DiffusionNexus.UI.Services.Download;

/// <summary>
/// The single-version early-access/paywall preflight shared by every surface that downloads
/// one version at a time — the detail panel's "Download This Version" and the toolbar's
/// "Download Lora" URL dialog. (The Browse tab keeps its own multi-selection variant in
/// <c>CivitaiBrowserViewModel.ApplyPreflightChoice</c>.) Shows the
/// <see cref="DownloadPreflightDialog"/> and applies the choice: waitlist the version,
/// open its Civitai page, or stop. Fails closed — with no window to own the dialog the
/// answer is "don't download" plus a status line, never a silent bypass into a transfer
/// Civitai is going to refuse.
/// </summary>
public sealed class GatedDownloadPreflight
{
    private readonly CivitaiWaitlist? _waitlist;
    private readonly IUnifiedLogger? _logger;

    public GatedDownloadPreflight(CivitaiWaitlist? waitlist, IUnifiedLogger? logger)
    {
        _waitlist = waitlist;
        _logger = logger;
    }

    /// <summary>Seam for "open in browser", same pattern as the browse tab's opener.</summary>
    public Action<string> UrlOpener { get; set; } = url =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Everything the preflight needs to know about the gated version.</summary>
    public sealed record Subject(
        int ModelId,
        string ModelName,
        string VersionLabel,
        CivitaiModelVersion Version,
        string Category,
        bool IsNsfw);

    /// <summary>
    /// What the caller does next: proceed with the download or stop, plus the line to show
    /// the user (null when the outcome needs no explanation, e.g. a plain Cancel).
    /// </summary>
    public sealed record Outcome(bool Proceed, string? StatusMessage);

    /// <summary>Shows the dialog and applies the user's choice.</summary>
    public async Task<Outcome> RunAsync(Subject subject)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            // Elsewhere the app treats a missing MainWindow as an error (DialogService
            // registration throws) — so hold the gate rather than silently dropping it.
            _logger?.Warn(LogCategory.Download, "LoraDownload",
                $"Gated download blocked without dialog (no owner window): {subject.ModelName} — {subject.VersionLabel}");
            return new(Proceed: false,
                $"'{subject.ModelName} — {subject.VersionLabel}' is paywalled on Civitai and the options "
                + "dialog could not be shown — download not attempted.");
        }

        var title = $"{subject.ModelName} — {subject.VersionLabel}";
        var dialog = subject.Version.IsPermanentlyPaid()
            ? new DownloadPreflightDialog([], permanentTitles: [title])
            : new DownloadPreflightDialog([title]);
        try
        {
            await dialog.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Download, "LoraDownload",
                $"Gated-download dialog failed for {title}: {ex.Message}");
            return new(Proceed: false, StatusMessage: null);
        }
        return ApplyChoice(dialog.Result, subject);
    }

    /// <summary>
    /// Applies the dialog choice. Internal (and dialog-free) so every branch is unit-testable;
    /// exercised through the surfaces' own wiring tests.
    /// </summary>
    internal Outcome ApplyChoice(DownloadPreflightResult choice, Subject subject)
    {
        switch (choice)
        {
            case DownloadPreflightResult.DownloadAnyway:
                return new(Proceed: true, StatusMessage: null);

            case DownloadPreflightResult.AddToWaitlist:
                if (_waitlist is null)
                {
                    _logger?.Warn(LogCategory.Download, "CivitaiWaitlist",
                        $"Waitlist unavailable — '{subject.ModelName} — {subject.VersionLabel}' not added.");
                    return new(Proceed: false, StatusMessage: null);
                }
                var added = _waitlist.TryAdd(
                    subject.ModelId, subject.ModelName, subject.Category, subject.IsNsfw, subject.Version);
                return new(Proceed: false, StatusMessage: added
                    ? $"Added '{subject.ModelName} — {subject.VersionLabel}' to the waitlist (Browse Civitai › Waitlist tab)."
                    : $"'{subject.ModelName} — {subject.VersionLabel}' is already on the waitlist.");

            case DownloadPreflightResult.OpenWebsite:
                if (subject.ModelId > 0)
                {
                    // civitai.com hides NSFW from unauthenticated visitors; route those to the mirror.
                    var host = subject.IsNsfw ? "civitai.red" : "civitai.com";
                    OpenUrl($"https://{host}/models/{subject.ModelId}?modelVersionId={subject.Version.Id}");
                    return new(Proceed: false, StatusMessage: null);
                }
                _logger?.Warn(LogCategory.Download, "LoraDownload",
                    $"Cannot open Civitai page: no model id for '{subject.ModelName} — {subject.VersionLabel}'.");
                return new(Proceed: false,
                    $"No Civitai model id for '{subject.ModelName}' — run 'Download Metadata' first.");

            default:
                _logger?.Info(LogCategory.Download, "LoraDownload",
                    $"Gated download declined by user: {subject.ModelName} — {subject.VersionLabel}");
                return new(Proceed: false, StatusMessage: null);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            UrlOpener(url);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "LoraDownload", $"Failed to launch browser for {url}: {ex.Message}");
        }
    }
}
