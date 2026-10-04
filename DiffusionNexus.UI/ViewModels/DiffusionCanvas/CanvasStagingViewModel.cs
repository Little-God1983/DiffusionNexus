using Avalonia.Media.Imaging;
using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// The staging strip: results arrive here as candidates, not commits.
///
/// Generation is iterative and most attempts lose. A surface that commits every result forces undo to
/// carry the whole workflow; a surface that asks makes undo a rarity. So nothing here reaches the canvas
/// without <see cref="AcceptCommand"/>, and the comparison gesture (<see cref="IsComparing"/>) matters
/// more than the buttons — a variant cannot be judged against nothing.
/// </summary>
public partial class CanvasStagingViewModel : ObservableObject
{
    /// <summary>Every candidate in the current batch, in generation order.</summary>
    public ObservableCollection<StagedCandidateViewModel> Candidates { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(AcceptAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardAllCommand))]
    private StagedCandidateViewModel? _current;

    /// <summary>
    /// True while the user holds the compare key. The surface hides the candidate so the canvas
    /// underneath shows through, which is the only way to judge a variant against what it would replace.
    /// </summary>
    [ObservableProperty]
    private bool _isComparing;

    /// <summary>True when anything at all is staged — drives the strip's visibility.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>
    /// The current candidate's bitmap, surfaced here so the canvas surface binds to one hop rather than
    /// through <c>Current.Image</c> — a path that silently produces nothing whenever Current is null.
    /// </summary>
    public Bitmap? CurrentImage => Current?.Image;

    /// <summary>Where the current candidate would land, for the surface's preview.</summary>
    public Rect CurrentRect => Current?.WorldRect ?? default;

    /// <summary>Human-readable position, e.g. "2 / 4".</summary>
    public string PositionText =>
        Current is null ? string.Empty : $"{Candidates.IndexOf(Current) + 1} / {Candidates.Count}";

    /// <summary>Raised when the user accepts a candidate; the canvas view model turns it into a raster.</summary>
    public event EventHandler<StagedCandidateViewModel>? CandidateAccepted;

    public CanvasStagingViewModel()
    {
        Candidates.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCandidates));
            OnPropertyChanged(nameof(PositionText));
            AcceptAllCommand.NotifyCanExecuteChanged();
            DiscardAllCommand.NotifyCanExecuteChanged();
            NextCommand.NotifyCanExecuteChanged();
            PreviousCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>
    /// Adds a batch: one dimmed slot per queued image, behind whatever is already staged, so the strip
    /// shows the shape of the queue before any result exists. Nothing staged is discarded: a candidate
    /// nobody has judged yet is not this method's to throw away (#598).
    /// </summary>
    /// <param name="select">
    /// Whether the batch's first slot becomes the selection. True for a batch that starts at once (the
    /// user just pressed Generate); false for one queued behind a running batch, which must not move
    /// the selection. With nothing selected the first slot is selected either way.
    /// </param>
    public IReadOnlyList<StagedCandidateViewModel> AddBatch(int count, Rect worldRect, bool select = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        // Numbered on from what is staged: the strip holds several batches at once, and a tile number
        // that repeats cannot be told apart in the strip or in the console.
        var last = Candidates.Count == 0 ? 0 : Candidates.Max(c => c.Ordinal);

        var created = new List<StagedCandidateViewModel>(count);
        for (var i = 0; i < count; i++)
        {
            var candidate = new StagedCandidateViewModel(last + i + 1, worldRect);
            Candidates.Add(candidate);
            created.Add(candidate);
        }

        if (select || Current is null)
            Current = created[0];
        return created;
    }

    /// <summary>The slot the running batch is on, which <see cref="Follow"/> moves the selection along with.</summary>
    private StagedCandidateViewModel? _followed;

    /// <summary>
    /// Tells the strip which slot is now rendering. The selection moves to it only while the user is on
    /// the slot that was rendering before (or on this one, or on nothing): stepping back to compare an
    /// earlier candidate is a choice, and the next image starting must not undo it.
    /// </summary>
    public void Follow(StagedCandidateViewModel candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var onTheRunningSlot = Current is null
            || ReferenceEquals(Current, _followed)
            || ReferenceEquals(Current, candidate);

        _followed = candidate;
        if (onTheRunningSlot)
            Current = candidate;
    }

    /// <summary>
    /// The batch is over. Its last slot stops being "the running slot", so a later batch starting does
    /// not pull the selection off a finished candidate the user is judging.
    /// </summary>
    public void EndFollow() => _followed = null;

    /// <summary>Removes a batch's slots that are still in the strip, e.g. when it leaves the queue unrun.</summary>
    /// <returns>How many slots were removed.</returns>
    public int RemoveBatch(IEnumerable<StagedCandidateViewModel> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var removed = 0;
        foreach (var candidate in batch.ToList())
        {
            if (Candidates.Contains(candidate))
            {
                Remove(candidate);
                removed++;
            }
        }

        return removed;
    }

    partial void OnCurrentChanging(StagedCandidateViewModel? value)
    {
        if (Current is { } previous)
            previous.PropertyChanged -= OnCurrentCandidatePropertyChanged;
    }

    partial void OnCurrentChanged(StagedCandidateViewModel? value)
    {
        if (value is not null)
            value.PropertyChanged += OnCurrentCandidatePropertyChanged;

        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(CurrentRect));
    }

    /// <summary>
    /// The current candidate's image arrives long after selection — it is generated while the slot is
    /// already on screen — so the surface's preview binding has to be re-raised when it lands.
    /// </summary>
    private void OnCurrentCandidatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StagedCandidateViewModel.Image))
            OnPropertyChanged(nameof(CurrentImage));
    }

    /// <summary>Steps to the next candidate, stopping at the end rather than wrapping.</summary>
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        var index = Current is null ? -1 : Candidates.IndexOf(Current);
        if (index + 1 < Candidates.Count)
            Current = Candidates[index + 1];
    }

    private bool CanGoNext() => Current is not null && Candidates.IndexOf(Current) + 1 < Candidates.Count;

    /// <summary>Steps to the previous candidate, stopping at the start rather than wrapping.</summary>
    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous()
    {
        var index = Current is null ? -1 : Candidates.IndexOf(Current);
        if (index > 0)
            Current = Candidates[index - 1];
    }

    private bool CanGoPrevious() => Current is not null && Candidates.IndexOf(Current) > 0;

    /// <summary>
    /// Accepts the current candidate onto the canvas. The candidate is detached from the strip first and
    /// the raster then takes ownership of its bitmap, so nothing disposes an image still bound into the
    /// visual tree.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept()
    {
        if (Current is not { } candidate || !candidate.IsReady)
            return;

        var index = Candidates.IndexOf(candidate);
        Candidates.Remove(candidate);
        SelectAfterRemoval(index);

        CandidateAccepted?.Invoke(this, candidate);
    }

    private bool CanAccept() => Current is { IsReady: true };

    /// <summary>Discards the current candidate, releasing its bitmap.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        if (Current is not { } candidate)
            return;

        Remove(candidate);
    }

    private bool CanDiscard() => Current is not null;

    /// <summary>Accepts every ready candidate, oldest first, leaving pending slots in place.</summary>
    [RelayCommand(CanExecute = nameof(HasAny))]
    private void AcceptAll()
    {
        foreach (var candidate in Candidates.Where(c => c.IsReady).ToList())
        {
            Current = candidate;
            Accept();
        }
    }

    /// <summary>Clears the whole strip.</summary>
    [RelayCommand(CanExecute = nameof(HasAny))]
    private void DiscardAll()
    {
        foreach (var candidate in Candidates.ToList())
            Remove(candidate);
    }

    private bool HasAny() => Candidates.Count > 0;

    /// <summary>Removes one candidate from the strip and disposes it, in that order.</summary>
    private void Remove(StagedCandidateViewModel candidate)
    {
        var index = Candidates.IndexOf(candidate);
        if (index < 0)
            return;

        // Only the selected slot's removal picks a new selection. Removing another one, e.g. a waiting
        // batch's slots or a cancelled batch's, must leave the candidate the user is judging selected.
        var wasCurrent = ReferenceEquals(candidate, Current);
        candidate.IsDiscarded = true;
        Candidates.Remove(candidate);
        if (wasCurrent)
        {
            SelectAfterRemoval(index);

            // Discarding the slot that is rendering is not stepping away from the run: the neighbour
            // was selected for the user, so the next image still takes the selection.
            if (ReferenceEquals(candidate, _followed))
                _followed = Current;
        }

        // Detach first, dispose second — disposing a bitmap that is still bound faults the render.
        candidate.Dispose();
    }

    private void SelectAfterRemoval(int removedIndex)
    {
        if (Candidates.Count == 0)
        {
            Current = null;
            return;
        }

        Current = Candidates[Math.Clamp(removedIndex, 0, Candidates.Count - 1)];
    }

    /// <summary>
    /// Removes every slot of the cancelled batch that the cancel made pointless: the ones that never
    /// started and the one that was in flight. Ready results stay, so a batch cancelled after two good
    /// images keeps both. Only <paramref name="batch"/>'s slots are looked at: the pending slots of a
    /// batch still waiting in the queue are not this cancel's.
    /// </summary>
    /// <remarks>
    /// These used to be kept and merely marked Cancelled. That left an empty dark tile in the strip —
    /// nothing to preview, nothing to accept, and the dimmed overlay that carries the status text is
    /// only shown while a slot is pending, so the tile did not even say why it was blank. A slot that
    /// can never hold an image has no business in a strip whose whole point is judging images.
    /// </remarks>
    /// <returns>How many slots were removed, for the log line.</returns>
    public int PruneAfterCancel(IEnumerable<StagedCandidateViewModel> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var doomed = batch
            .Where(c => Candidates.Contains(c) && (c.IsPending || c.State == StagedCandidateState.Cancelled))
            .ToList();

        foreach (var candidate in doomed)
            Remove(candidate);

        return doomed.Count;
    }

    /// <summary>Notifies the accept/discard commands after a candidate's state changes.</summary>
    public void RefreshCommands()
    {
        AcceptCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        AcceptAllCommand.NotifyCanExecuteChanged();
        DiscardAllCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
    }
}
