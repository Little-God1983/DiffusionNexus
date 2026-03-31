using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Reusable ViewModel that exposes the readiness state of a single <see cref="ComfyUIFeature"/>.
/// 
/// <para>
/// Embed an instance of this ViewModel in any tab/panel that depends on ComfyUI.
/// It handles the async check, exposes bindable properties for the UI (server online,
/// missing nodes, missing models, warnings), and provides a <see cref="CheckReadinessCommand"/>
/// the user can trigger to re-check.
/// </para>
/// 
/// <example>
/// In your parent ViewModel constructor:
/// <code>
/// Readiness = new ComfyUIReadinessViewModel(readinessService, ComfyUIFeature.Captioning);
/// </code>
/// Then in XAML bind to <c>Readiness.IsReady</c>, <c>Readiness.MissingRequirements</c>, etc.
/// </example>
/// </summary>
public sealed partial class ComfyUIReadinessViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<ComfyUIReadinessViewModel>();

    private readonly IComfyUIReadinessService? _readinessService;
    private readonly ComfyUIFeature _feature;

    private bool _isChecking;
    private bool _isReady;
    private bool _isServerOnline;
    private bool _hasChecked;
    private IReadOnlyList<string> _missingRequirements = [];
    private IReadOnlyList<string> _warnings = [];
    private string? _statusMessage;

    /// <summary>
    /// Creates a new readiness ViewModel for the specified feature.
    /// </summary>
    /// <param name="readinessService">The unified readiness service. May be <c>null</c> if ComfyUI is not configured.</param>
    /// <param name="feature">The ComfyUI feature to check prerequisites for.</param>
    public ComfyUIReadinessViewModel(IComfyUIReadinessService? readinessService, ComfyUIFeature feature)
    {
        _readinessService = readinessService;
        _feature = feature;

        CheckReadinessCommand = new AsyncRelayCommand(CheckReadinessAsync);

        // Expose the static requirements for informational display
        FeatureDisplayName = readinessService?.GetRequirements(feature)?.DisplayName ?? feature.ToString();
    }

    #region Properties

    /// <summary>
    /// Human-readable name of the feature (from the registry).
    /// </summary>
    public string FeatureDisplayName { get; }

    /// <summary>
    /// Whether a readiness check is currently in progress.
    /// </summary>
    public bool IsChecking
    {
        get => _isChecking;
        private set => SetProperty(ref _isChecking, value);
    }

    /// <summary>
    /// Whether the feature is ready to execute (server online + all prerequisites met).
    /// </summary>
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

    /// <summary>
    /// Inverse of <see cref="IsReady"/> for convenient XAML binding.
    /// </summary>
    public bool IsNotReady => !IsReady;

    /// <summary>
    /// Whether the ComfyUI server is reachable.
    /// </summary>
    public bool IsServerOnline
    {
        get => _isServerOnline;
        private set => SetProperty(ref _isServerOnline, value);
    }

    /// <summary>
    /// Whether at least one check has been performed.
    /// </summary>
    public bool HasChecked
    {
        get => _hasChecked;
        private set => SetProperty(ref _hasChecked, value);
    }

    /// <summary>
    /// Blocking problems preventing the feature from running. Empty when ready.
    /// </summary>
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

    /// <summary>
    /// Whether there are any blocking missing requirements.
    /// </summary>
    public bool HasMissingRequirements => MissingRequirements.Count > 0;

    /// <summary>
    /// Non-blocking warnings (e.g. auto-download model not yet present).
    /// </summary>
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

    /// <summary>
    /// Whether there are any non-blocking warnings.
    /// </summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>
    /// Short status message for display (e.g. "Checking…", "Ready", "Server offline").
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Command to trigger a readiness check. Safe to call multiple times.
    /// </summary>
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
            IsServerOnline = false;
            MissingRequirements = ["ComfyUI integration is not configured."];
            Warnings = [];
            StatusMessage = "Not configured";
            HasChecked = true;
            return;
        }

        IsChecking = true;
        StatusMessage = "Checking ComfyUI…";

        try
        {
            var result = await _readinessService.CheckFeatureAsync(_feature, ct);

            IsServerOnline = result.IsServerOnline;
            IsReady = result.IsReady;
            MissingRequirements = result.MissingRequirements;
            Warnings = result.Warnings;

            StatusMessage = result.IsReady
                ? "Ready"
                : result.IsServerOnline
                    ? "Missing prerequisites"
                    : "Server offline";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Check cancelled";
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Readiness check failed for {Feature}", _feature);
            IsReady = false;
            IsServerOnline = false;
            MissingRequirements = [$"Readiness check failed: {ex.Message}"];
            Warnings = [];
            StatusMessage = "Check failed";
        }
        finally
        {
            IsChecking = false;
            HasChecked = true;
        }
    }

    #endregion
}
