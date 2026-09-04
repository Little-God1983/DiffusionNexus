using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>Lifecycle of one staged candidate.</summary>
public enum StagedCandidateState
{
    /// <summary>Queued but not started — the dimmed slot in the staging strip.</summary>
    Pending,

    /// <summary>The backend is loading a model for this candidate.</summary>
    Loading,

    /// <summary>The backend is sampling this candidate.</summary>
    Sampling,

    /// <summary>Finished; <see cref="StagedCandidateViewModel.Image"/> holds the result.</summary>
    Ready,

    /// <summary>The generation failed; <see cref="StagedCandidateViewModel.StatusText"/> says why.</summary>
    Failed,

    /// <summary>The batch was cancelled before this candidate ran (or while it was running).</summary>
    Cancelled,
}

/// <summary>
/// One result waiting for a verdict. Candidates never touch the canvas on their own — the user accepts
/// or discards each one. That is what keeps undo a rarity rather than the thing the whole workflow rests
/// on: a surface that commits every result forces undo to carry the workflow, a surface that asks does not.
/// </summary>
public partial class StagedCandidateViewModel : ObservableObject, IDisposable
{
    /// <summary>Position in the batch, 1-based, shown on the slot.</summary>
    public int Ordinal { get; }

    /// <summary>
    /// Where this candidate will land if accepted — the bounding box as it was when the batch started,
    /// so moving the box mid-batch cannot retarget results that were already generated.
    /// </summary>
    public Rect WorldRect { get; }

    [ObservableProperty]
    private StagedCandidateState _state = StagedCandidateState.Pending;

    [ObservableProperty]
    private string _statusText = "Queued";

    /// <summary>The decoded result, or null until it arrives.</summary>
    [ObservableProperty]
    private Bitmap? _image;

    /// <summary>The encoded PNG, kept so accepting can write the file without re-encoding.</summary>
    [ObservableProperty]
    private byte[]? _pngBytes;

    /// <summary>Seed the backend actually used, echoed back for provenance.</summary>
    [ObservableProperty]
    private long? _seed;

    /// <summary>1-based sampling step, meaningful while <see cref="State"/> is Sampling.</summary>
    [ObservableProperty]
    private int _stepCurrent;

    /// <summary>Total sampling steps, meaningful while <see cref="State"/> is Sampling.</summary>
    [ObservableProperty]
    private int _stepTotal;

    /// <summary>True while this slot is still waiting on the backend — the strip dims these.</summary>
    public bool IsPending => State is StagedCandidateState.Pending
        or StagedCandidateState.Loading
        or StagedCandidateState.Sampling;

    /// <summary>True once the candidate can be accepted onto the canvas.</summary>
    public bool IsReady => State == StagedCandidateState.Ready;

    /// <summary>True once this candidate's bitmap has been released.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Raised as the candidate is disposed. Exists so the staging view model's ordering contract —
    /// detach from the collection first, dispose second — is observable from a test.
    /// </summary>
    public event EventHandler? Disposed;

    public StagedCandidateViewModel(int ordinal, Rect worldRect)
    {
        Ordinal = ordinal;
        WorldRect = worldRect;
    }

    partial void OnStateChanged(StagedCandidateState value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsReady));
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;

        // Callers must have detached this candidate from the bound collection already: disposing a
        // bitmap still bound into the visual tree faults the render (SelectableImageResultsViewModel
        // records the same lesson).
        var image = Image;
        Image = null;
        PngBytes = null;

        try
        {
            image?.Dispose();
        }
        catch (Exception)
        {
            // Releasing a bitmap must never be able to take down a teardown path: this runs from
            // DiscardAll and from the canvas view model's own Dispose, both of which are called during
            // shutdown. A bitmap we cannot release is a leak, not a crash.
        }

        Disposed?.Invoke(this, EventArgs.Empty);
        GC.SuppressFinalize(this);
    }
}
