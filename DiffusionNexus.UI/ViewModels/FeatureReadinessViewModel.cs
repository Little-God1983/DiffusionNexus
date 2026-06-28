using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Reusable ViewModel that exposes the readiness state of a single <see cref="Feature"/>.
/// Backend-agnostic — the underlying <see cref="IFeatureReadinessService"/> picks the active
/// backend via <see cref="IFeatureBackendRouter"/>, so this VM never has to care whether the
/// answer came from ComfyUI or local inference.
/// </summary>
public sealed partial class FeatureReadinessViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<FeatureReadinessViewModel>();

    private readonly IFeatureReadinessService? _readinessService;
    private readonly Feature _feature;

    private bool _isChecking;
    private bool _isReady;
    private bool _isBackendOnline;
    private bool _hasChecked;
    private string? _activeBackendName;
    private IReadOnlyList<string> _missingRequirements = [];
    private IReadOnlyList<string> _warnings = [];
    private string? _statusMessage;
    private BackendInfo? _selectedBackend;
    private int _checkGeneration;

    /// <summary>
    /// Creates a new readiness ViewModel for the specified feature.
    /// </summary>
    /// <param name="readinessService">The unified readiness service. May be <c>null</c> if no backend is configured.</param>
    /// <param name="feature">The feature to check prerequisites for.</param>
    /// <param name="allowBackendSelection">
    /// When <c>true</c>, the panel shows a backend picker (e.g. "ComfyUI" vs "Diffusion Nexus
    /// Core") and readiness is evaluated against the chosen backend. Defaults to <c>false</c>
    /// so existing single-backend tools are unaffected.
    /// </param>
    public FeatureReadinessViewModel(
        IFeatureReadinessService? readinessService,
        Feature feature,
        bool allowBackendSelection = false)
    {
        _readinessService = readinessService;
        _feature = feature;
        AllowBackendSelection = allowBackendSelection;

        CheckReadinessCommand = new AsyncRelayCommand(CheckReadinessAsync);

        FeatureDisplayName = readinessService?.GetRequirements(feature)?.DisplayName ?? feature.ToString();

        AvailableBackends = readinessService?.GetAvailableBackends() ?? [];

        // Seed the picker with the feature's default backend (kept current behaviour). Set the
        // backing field directly so seeding does not kick off a redundant readiness check before
        // the panel is even shown.
        var defaultKind = readinessService?.GetDefaultBackend(feature);
        _selectedBackend = AvailableBackends.FirstOrDefault(b => b.Kind == defaultKind)
                           ?? AvailableBackends.FirstOrDefault();
    }

    #region Properties

    /// <summary>Human-readable name of the feature (from the registry).</summary>
    public string FeatureDisplayName { get; }

    /// <summary>Whether the backend picker is shown for this feature.</summary>
    public bool AllowBackendSelection { get; }

    /// <summary>The backends the user may pick from (empty when selection is unavailable).</summary>
    public IReadOnlyList<BackendInfo> AvailableBackends { get; }

    /// <summary>
    /// The backend the user has picked to run this feature on. Changing it re-runs the readiness
    /// check against the newly selected backend.
    /// </summary>
    public BackendInfo? SelectedBackend
    {
        get => _selectedBackend;
        set
        {
            if (SetProperty(ref _selectedBackend, value) && value is not null)
            {
                // Re-evaluate readiness for the newly chosen backend.
                _ = CheckReadinessAsync();
            }
        }
    }

    /// <summary>Whether a readiness check is currently in progress.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        private set => SetProperty(ref _isChecking, value);
    }

    /// <summary>Whether the feature is ready to execute on its currently selected backend.</summary>
    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (SetProperty(ref _isReady, value))
            {
                OnPropertyChanged(nameof(IsNotReady));
            }
        }
    }

    /// <summary>Inverse of <see cref="IsReady"/> for convenient XAML binding.</summary>
    public bool IsNotReady => !IsReady;

    /// <summary>
    /// Whether the active backend is reachable / loaded (ComfyUI server up, or local native
    /// library loaded).
    /// </summary>
    public bool IsBackendOnline
    {
        get => _isBackendOnline;
        private set => SetProperty(ref _isBackendOnline, value);
    }

    /// <summary>Whether at least one check has been performed.</summary>
    public bool HasChecked
    {
        get => _hasChecked;
        private set => SetProperty(ref _hasChecked, value);
    }

    /// <summary>Display name of the backend that answered the most recent check.</summary>
    public string? ActiveBackendName
    {
        get => _activeBackendName;
        private set => SetProperty(ref _activeBackendName, value);
    }

    /// <summary>Blocking problems preventing the feature from running. Empty when ready.</summary>
    public IReadOnlyList<string> MissingRequirements
    {
        get => _missingRequirements;
        private set
        {
            if (SetProperty(ref _missingRequirements, value))
            {
                OnPropertyChanged(nameof(HasMissingRequirements));
            }
        }
    }

    /// <summary>Whether there are any blocking missing requirements.</summary>
    public bool HasMissingRequirements => MissingRequirements.Count > 0;

    /// <summary>Non-blocking warnings (e.g. auto-download model not yet present).</summary>
    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set
        {
            if (SetProperty(ref _warnings, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
            }
        }
    }

    /// <summary>Whether there are any non-blocking warnings.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Short status message for display (e.g. "Checking…", "Ready", "Server offline").</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    #region Commands

    /// <summary>Command to trigger a readiness check. Safe to call multiple times.</summary>
    public IAsyncRelayCommand CheckReadinessCommand { get; }

    #endregion

    #region Methods

    /// <summary>
    /// Performs the readiness check asynchronously.
    /// Called by <see cref="CheckReadinessCommand"/> or directly from code.
    /// </summary>
    public async Task CheckReadinessAsync(CancellationToken ct = default)
    {
        if (_readinessService is null)
        {
            IsReady = false;
            IsBackendOnline = false;
            MissingRequirements = ["Readiness service is not configured."];
            Warnings = [];
            StatusMessage = "Not configured";
            HasChecked = true;
            return;
        }

        // Switching the backend dropdown (or pressing Check) fires a fresh check while a prior one
        // may still be awaiting a slow ComfyUI probe. Stamp each run so a stale result that completes
        // out of order can't overwrite the newest selection's result. (All runs on the UI thread, so
        // a plain counter is safe.)
        var generation = ++_checkGeneration;
        IsChecking = true;
        StatusMessage = "Checking…";

        try
        {
            var result = await _readinessService.CheckAsync(_feature, SelectedBackend?.Kind, ct);

            if (generation != _checkGeneration)
                return; // superseded by a newer check

            IsBackendOnline = result.IsBackendOnline;
            IsReady = result.IsReady;
            ActiveBackendName = result.ActiveBackendName;
            MissingRequirements = result.MissingRequirements;
            Warnings = result.Warnings;

            StatusMessage = result.IsReady
                ? "Ready"
                : result.IsBackendOnline
                    ? "Missing prerequisites"
                    : "Backend offline";
        }
        catch (OperationCanceledException)
        {
            if (generation == _checkGeneration)
                StatusMessage = "Check cancelled";
        }
        catch (Exception ex)
        {
            if (generation != _checkGeneration)
                return; // superseded — don't clobber the newer result with this one's error

            Logger.Warning(ex, "Readiness check failed for {Feature}", _feature);
            IsReady = false;
            IsBackendOnline = false;
            MissingRequirements = [$"Readiness check failed: {ex.Message}"];
            Warnings = [];
            StatusMessage = "Check failed";
        }
        finally
        {
            // Only the newest check owns the IsChecking/HasChecked flags; an older superseded run
            // must not flip IsChecking off while the newer one is still in flight.
            if (generation == _checkGeneration)
            {
                IsChecking = false;
                HasChecked = true;
            }
        }
    }

    #endregion
}
