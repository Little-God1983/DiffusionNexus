# Diffusion Canvas generation queue — design (#598, #518 region F)

Issue: #598, sub-issue of #518. One PR, branch `feature/canvas-generation-queue`. Builds on staging
(slice 1), the layer stack (#594) and the inpaint mask (#595).

## Goal

Generate enqueues a batch instead of blocking, and a status bar owns the queue: how much is left, how
fast it runs, when it will be done. Cancel already stops work (slice 1); it now stops one batch of
several.

## Decisions taken with the owner

| Question | Decision |
|---|---|
| What happens to candidates still in the strip when a batch is added? | **They stay.** The strip appends; nothing unjudged is discarded unasked. |
| What does Cancel stop? | **The running batch only.** The next queued batch starts. Queued batches are removed from the queue list. |

## What the user gets

- **Generate stays enabled** while a batch runs. Each press adds one batch to the queue. One batch runs
  at a time, in the order pressed: the GPU holds a single model.
- **A batch is frozen when Generate is pressed**: prompt, negative prompt, sampling settings, seed,
  LoRAs, model, backend, the box's position and size, the layers under the box, the mask and the denoise.
  Moving the box, accepting a result, switching the model or typing the next prompt does not change a
  batch that is already queued.
  - Checked at once, as today: an empty prompt, no model, a box off the model's lattice.
  - Checked when the batch starts: the backend's readiness, the region composite, the mask raster. A
    batch refused then leaves the queue, its slots are removed, and the status line and the console
    say why. With an empty queue a batch starts immediately, so nothing feels different from today.
- **The strip appends.** A new batch adds its dimmed "Queued" slots behind what is there. The slots
  appear when Generate is pressed, so the strip shows the queue's shape.
  - Pressing Generate with an idle queue selects the batch's first slot, as today.
  - A batch added while another runs does not move the selection.
- **The selection is no longer stolen.** The strip follows the running image only while the selection
  is on the running slot (or on nothing). Step back to compare and it stays where it was put; step onto
  the running slot and it follows again.
- **Status bar** along the bottom of the canvas view:
  - the status line (moved from the generate panel's footer);
  - while the queue is busy: `Image 2/4 · 2 batches queued (8 images)`, the throughput and the ETA;
  - **Queue**: a flyout listing the running and the queued batches (number, image count, start of the
    prompt, state). The running one has Cancel, each queued one has Remove, and Clear queue removes
    every queued one;
  - **Cancel** (moved from beside Generate): stops the running batch; the next one starts.
- **Throughput**: `2.1 it/s` when the backend reports steps (Core); `1.8 s/it` below one step per
  second; `12 s/image` when the backend reports no steps (the Engine) and an image has finished;
  nothing before that.
- **ETA** covers the whole queue. With a step rate: the steps left in the running image at that rate,
  plus the images not started at the measured time per step (their own step counts), or at the step
  rate until an image has finished. Without a step rate: the measured time per step times the steps
  left. Before either is known: `ETA —`, never a guess. It is recomputed on every progress event and
  once a second by the view, so an image in flight counts down on a backend that reports no steps.
- **The pace** is measured on the latest finished image, from its first reported step to its end, so a
  cold start's model load is not counted (a backend without steps can only measure the whole image; a
  warm image then replaces the cold one). It is kept for the next batch only when that batch runs on
  the same backend, model and box size, and dropped otherwise and when the queue goes idle.
  It is counted in the steps the backend really samples: image to image on Core samples only
  steps × denoise and reports that smaller total, so the ETA scales each image's asked-for steps by
  the running batch's share (reported total / asked-for steps; 1 until the batch reports one, and for
  a backend without steps). Waiting batches are estimated with the running batch's pace and share.
  `s/image` is the per-step pace times the running batch's steps, so it agrees with the ETA.
- A cancelled batch stays the running one until its backend has unwound; its row reads
  `Cancelling…` and its Cancel is disabled meanwhile.
- Discarding every slot of the running batch while it is still starting up (before its first image)
  cancels it; after that each discarded slot is skipped.
- The view's 1 s ETA timer runs only while the queue is busy.
- Slots are numbered on from what is staged, so two batches in the strip never share a number.
- The step that ran before its first report is added to the measured pace at the reported rate
  (it matters for 1- to 8-step models).
- A batch that did not run, or a press refused while a batch runs, leaves its reason in the status
  bar's notice (`BatchNotice`) until the next accepted press; the status line itself belongs to the
  running batch.
- When a batch finds its backend or model unavailable, the batches already queued for the same one
  skip without asking again. A new press asks again.
- The queue list scrolls (max height 320).
- Removing a queued batch removes its slots from the strip. Discarding a queued slot by hand still
  works: the count and the ETA leave it out at once, the batch skips it, and a waiting batch with
  every slot discarded leaves the queue.
- Removing slots that are not the selected one (a removed or cancelled batch's) never moves the
  selection.

## Components

### `CanvasGenerationQueue` (new, `ViewModels/DiffusionCanvas/`)

Owns the batch list, the worker and the numbers. It does not know what a batch does: each batch is
enqueued with a `CanvasBatchWork` of closures (`Run`, `OnDropped`, `OnCancelling`, `PendingImages`),
so there is no untyped payload to cast. Single-threaded by contract (the UI thread); it has no lock.

- `Batches` (`ObservableCollection<CanvasQueuedBatchViewModel>`, running first), `IsBusy`.
- `Enqueue(batch, work)` returns a task that completes when the batch has finished, was cancelled or
  was removed. The first enqueue starts the worker; the worker runs batches until the list is empty.
- `CancelRunning()` tells the batch's owner (`OnCancelling`) and cancels the batch's token. The run
  epoch moves here from the canvas view model with its invariant: never cancel without nulling.
- `Remove(batch)` (queued: drop and `OnDropped`; running: `CancelRunning`), `ClearQueuedCommand`.
- `SlotsChanged()`: the owner's slots changed; re-read the counts and drop waiting batches with
  nothing left. `Tick()`: re-read the ETA against the clock.
- Progress intake: `ImageStarted`, `ReportStep(step, total, itPerSecond)`, `ImageFinished(succeeded)`,
  `ImageSkipped`.
- Readout: `QueueText`, `ThroughputText`, `EtaText`, computed from the above and an injectable clock.
- Neither a batch that throws nor a listener that throws on one of the queue's notifications stops the
  worker or leaves it marked as running with nothing behind it.
- Every transition traces: enqueued, started, finished, cancel requested, cancelled, removed, cleared,
  left for having no slots, queue empty.

### `CanvasQueuedBatchViewModel` (new)

Number, image count, steps, pace key, prompt preview, `IsRunning`, `ImagesDone`, `StateText` and its
own `RemoveCommand` (so the flyout needs no ancestor binding).

### `DiffusionCanvasViewModel` (changed)

- `GenerateCommand` allows concurrent executions. It captures a batch on the UI thread and returns the
  queue's task for it, so awaiting the command still means "this batch is over".
- The old Generate body becomes `RunQueuedBatchAsync(batch, token)`, reading the captured values
  instead of the live controls. The backend and the descriptor are resolved by the captured keys.
- `IsGenerating` mirrors `Queue.IsBusy`; `CanGenerate` no longer depends on it.
- `Cancel` is `Queue.CancelRunning()`; the batch's `OnCancelling` sets the status and prunes that
  batch's unfinished slots only, so the button and the queue list's row take the same path.
- Changes to `Staging.Candidates` are forwarded to `Queue.SlotsChanged()`.
- `Dispose` cancels the running batch and drops the queued ones.

### `CanvasStagingViewModel` (changed)

- `BeginBatch` becomes `AddBatch(count, rect, select)`: appends, never discards.
- `Follow(candidate)` implements the selection rule; `EndFollow()` at the end of a batch.
- `PruneAfterCancel` and the new `RemoveBatch` take the batch's candidates, so one batch's cancel
  cannot remove another batch's queued slots.

### View

A third row in `DiffusionCanvasView.axaml` holds the status bar. The panel footer keeps the batch
count and Generate. The view runs a one-second `DispatcherTimer` while attached that calls
`Queue.Tick()`.

## Testing

- Unit, queue alone: order, enqueue while running, cancel then next, remove queued, clear queued, a
  throwing batch, `IsBusy`, the ETA on both progress shapes, the throughput texts.
- Unit, canvas view model: Generate enabled mid-run, a second batch frozen at its own press (prompt,
  box, region), the strip appends, cancel keeps the queued batch and its slots, removing a queued
  batch removes its slots, a refused queued batch removes only its slots, the selection stays where
  the user put it, an all-discarded batch is skipped.
- Existing batch, inpaint and panel tests keep passing unchanged except where they pin the replaced
  strip behaviour.
- GUI smoke with a real Core run: queue two batches, watch it/s and ETA, cancel the first, remove one.

## Out of scope

Reordering queued batches; pausing; a queue that survives a restart; parallel batches; a pace kept
per model for batches that have not started (they are estimated at the running batch's pace).
