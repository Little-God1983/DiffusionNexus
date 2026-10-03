# Diffusion Canvas Layer Stack Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Diffusion Canvas's accepted results become a layer stack: a right-hand panel to reorder, show/hide, rename, lock and delete them, with an opacity/provenance inspector. Visibility and opacity reach both the screen and the image the model is given. The list is a shared control that the Image Editor moves onto too.

**Architecture:** `GenerationFrameViewModel` gains layer properties. A new `CanvasLayerStackViewModel` sub-VM owns the stack's rules and a top-first mirror of `Frames`. The compositor, hit test and surface honour `IsVisible`/`Opacity`, which are added to `ICanvasRaster` as default interface members. A new `LayerStackPanel` control in `Views/Controls/` renders any `ILayerStackItem` list. The Image Editor's inline layer list is replaced by it, with a guard against the `ListBox` null write-back during `SyncLayers`.

**Tech Stack:** .NET 10, Avalonia 11.3.13 (compiled bindings on by default), CommunityToolkit.Mvvm, SkiaSharp 3.119.4, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-10-02-canvas-layer-stack-design.md` (issue #594, sub-issue of #518). Branch `feature/canvas-layer-stack` (exists, holds the spec commit `00490570`).

## Global Constraints

- Working directory for every command: `E:\Repos\DiffusionNexus`. Never use un-pathed searches; they can land in the SDK repo.
- One PR for #594 on `feature/canvas-layer-stack` → `develop`. Never merge it; the owner merges.
- Commit and push after every task (owner rule). End every commit message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Keep each file's existing line endings. Before each push, compare `git diff --cached --numstat` with `git diff --cached -w --numstat`. If a file shows hundreds of changed lines in the first and few in the second, its line endings flipped; restore them before committing. (`GenerationFrameViewModel.cs` and `LayerPanelViewModel.cs` are CRLF; `ImageEditView.axaml` is LF.)
- Every user-visible operation is traced to the Unified Console (standing repo rule). The canvas uses its existing `EmitInfo`; the editor uses `IUnifiedLogger` with source `"ImageEditor"`.
- Numbers shown in the UI are formatted with `CultureInfo.InvariantCulture` in the view model, never with XAML `StringFormat`. The dev machine is German, and `StringFormat` renders `1,0`.
- Lock means **protect from removal**: a locked layer cannot be deleted, and Clear canvas keeps it. Reorder, rename, hide and opacity stay allowed. In the Image Editor, lock also keeps its existing meaning: Move/Transform refuses a locked layer.
- New reusable controls go in `DiffusionNexus.UI/Views/Controls/`. Add a row to `DiffusionNexus.UI/REUSABLES.md` in the same commit.
- Out of scope: undo, drag-reorder, canvas persistence, blend modes, other layer kinds, and a keyboard delete for layers. Delete stays the staging-discard key.
- Build: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -v q`. Tests: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "<filter>" -v q`.

## Review Focus

1. **Changing a layer while Generate is compositing.** The user hides a layer or drags opacity mid-composite. Expected: the run uses the values from the moment Generate was pressed. → Task 2 `CanvasRasterSnapshot` test; Task 4 snapshots on the UI thread.
2. **Deleting the selected layer.** Expected: the selection moves to the layer that took its place, so the inspector does not go blank. → Task 3 test `DeleteSelected_SelectsTheLayerThatTookItsPlace`.
3. **Image Editor `SyncLayers` behind a `ListBox`.** Clearing the bound collection writes `null` back into `SelectedLayer`, which would clear the editor core's active layer. Expected: the active layer survives the sync. → Task 8 test `SyncLayers_IgnoresTheListBoxNullWriteBackAndKeepsTheActiveLayer`.
4. **Accepting a candidate while a lower layer is selected.** Expected: the new layer still lands on top and becomes selected. → Task 3 test `AddAccepted_LandsOnTopEvenWhenALowerLayerIsSelected`.
5. **A hidden or 0 % layer under the box.** Expected: the readout says text to image and Generate sends no init image. → Task 4 tests `HidingTheLayerUnderTheBox_*` and `Generate_OverAHiddenLayerSendsNoInitImage`.

---

### Task 1: Layer properties on the canvas raster

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/ILayerStackItem.cs`
- Create: `DiffusionNexus.UI/ViewModels/DiffusionCanvas/CanvasLayerKind.cs`
- Modify: `DiffusionNexus.UI/DiffusionCanvas/ICanvasRaster.cs`
- Modify: `DiffusionNexus.UI/ViewModels/DiffusionCanvas/GenerationFrameViewModel.cs`
- Test: `DiffusionNexus.Tests/DiffusionCanvas/GenerationFrameLayerTests.cs`

**Interfaces:**
- Produces:
  - `interface ILayerStackItem : INotifyPropertyChanged { string Name {get;set;} bool IsVisible {get;set;} bool IsLocked {get;set;} string OpacityText {get;} Bitmap? Thumbnail {get;} }` in namespace `DiffusionNexus.UI.ViewModels`
  - `static class LayerStackNaming { const int MaxLength = 64; static string Resolve(string? proposed, string current); }`
  - `enum CanvasLayerKind { Raster }`
  - `ICanvasRaster.IsVisible` (default `true`) and `ICanvasRaster.Opacity` (default `1.0`)
  - On `GenerationFrameViewModel`: `Kind`, `Name`, `IsVisible`, `Opacity` (clamped 0–1, NaN → 1), `OpacityPercent` (int), `OpacityText`, `IsLocked`, `Thumbnail` (= `FrameImage`) and `ProvenanceText`

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/DiffusionCanvas/GenerationFrameLayerTests.cs`:

```csharp
using System.Globalization;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>An accepted result is now a layer: the properties the layer stack (#594) edits.</summary>
public class GenerationFrameLayerTests
{
    [Fact]
    public void ANewFrameIsAVisibleOpaqueUnlockedRasterLayer()
    {
        var frame = new GenerationFrameViewModel();

        frame.Kind.Should().Be(CanvasLayerKind.Raster);
        frame.IsVisible.Should().BeTrue();
        frame.Opacity.Should().Be(1.0);
        frame.IsLocked.Should().BeFalse();
        frame.Name.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1.5, 1.0)]
    [InlineData(-0.2, 0.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.25, 0.25)]
    public void OpacityIsClampedToTheUnitRange(double assigned, double expected)
    {
        var frame = new GenerationFrameViewModel { Opacity = assigned };

        frame.Opacity.Should().Be(expected);
    }

    [Fact]
    public void OpacityPercentRoundTripsAndRaisesTheDerivedProperties()
    {
        var frame = new GenerationFrameViewModel();
        var raised = new List<string?>();
        frame.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        frame.OpacityPercent = 50;

        frame.Opacity.Should().Be(0.5);
        frame.OpacityText.Should().Be("50%");
        raised.Should().Contain([nameof(GenerationFrameViewModel.Opacity),
            nameof(GenerationFrameViewModel.OpacityPercent), nameof(GenerationFrameViewModel.OpacityText)]);
    }

    [Fact]
    public void ProvenanceTextIsInvariantUnderAGermanCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var frame = new GenerationFrameViewModel
            {
                Seed = 1234567, Width = 1024, Height = 768, CanvasX = 1536.5, CanvasY = -64,
            };

            frame.ProvenanceText.Should().Be("Seed 1234567 · 1024×768 at (1537, -64)");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ProvenanceTextSaysWhenTheSeedIsUnknown()
    {
        new GenerationFrameViewModel { Width = 512, Height = 512 }
            .ProvenanceText.Should().StartWith("Seed unknown · 512×512");
    }

    [Theory]
    [InlineData("  Sky  ", "Old", "Sky")]
    [InlineData("", "Old", "Old")]
    [InlineData("   ", "Old", "Old")]
    [InlineData(null, "Old", "Old")]
    [InlineData("two\r\nlines", "Old", "two  lines")]
    public void LayerStackNaming_TrimsAndRevertsBlankNames(string? proposed, string current, string expected)
    {
        LayerStackNaming.Resolve(proposed, current).Should().Be(expected);
    }

    [Fact]
    public void LayerStackNaming_CapsTheLength()
    {
        LayerStackNaming.Resolve(new string('x', 200), "Old").Should().HaveLength(LayerStackNaming.MaxLength);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail to compile**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~GenerationFrameLayerTests" -v q`
Expected: build errors, because `CanvasLayerKind`, `LayerStackNaming`, `Opacity` and the others do not exist yet.

- [ ] **Step 3: Add `ILayerStackItem` and `LayerStackNaming`**

Create `DiffusionNexus.UI/ViewModels/ILayerStackItem.cs`:

```csharp
using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// One row of a <c>LayerStackPanel</c>. Implemented by the Image Editor's <see cref="LayerViewModel"/>
/// and by the Diffusion Canvas's <c>GenerationFrameViewModel</c>, so both screens share one layer list.
/// </summary>
/// <remarks>
/// The interface covers only what a row edits in place. Reordering and deleting stay commands on the
/// host's own view model, which is where each screen's rules live (a locked layer cannot be deleted; the
/// editor cannot delete its last layer).
/// </remarks>
public interface ILayerStackItem : INotifyPropertyChanged
{
    /// <summary>Display name. Rename commits through <see cref="LayerStackNaming.Resolve"/>.</summary>
    string Name { get; set; }

    /// <summary>Whether the layer is shown.</summary>
    bool IsVisible { get; set; }

    /// <summary>Whether the layer is protected from removal.</summary>
    bool IsLocked { get; set; }

    /// <summary>Opacity as display text, already formatted invariantly ("75%").</summary>
    string OpacityText { get; }

    /// <summary>Row thumbnail, or null while the layer has no pixels.</summary>
    Bitmap? Thumbnail { get; }
}

/// <summary>The one rule for what a rename commits.</summary>
public static class LayerStackNaming
{
    /// <summary>Longest name kept. A pasted paragraph would otherwise push every row's controls off-screen.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The name to store for <paramref name="proposed"/>: control characters become spaces (a pasted
    /// line break would otherwise land in a single-line row), the result is trimmed and capped, and a
    /// blank result keeps <paramref name="current"/>. A layer always has a name.
    /// </summary>
    public static string Resolve(string? proposed, string current)
    {
        if (proposed is null)
            return current;

        var cleaned = new string(proposed.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (cleaned.Length == 0)
            return current;

        return cleaned.Length > MaxLength ? cleaned[..MaxLength].TrimEnd() : cleaned;
    }
}
```

Note: `"two\r\nlines"` becomes `"two  lines"`, because each control character turns into one space and only the ends are trimmed. That is what the test expects.

- [ ] **Step 4: Add `CanvasLayerKind`**

Create `DiffusionNexus.UI/ViewModels/DiffusionCanvas/CanvasLayerKind.cs`:

```csharp
namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// What a canvas layer holds. Issue #518 region D names four kinds; only <see cref="Raster"/> exists so
/// far. Inpaint masks (#595), control layers (#596) and regional prompts (#597) add theirs here.
/// </summary>
public enum CanvasLayerKind
{
    /// <summary>Pixels: an accepted generation result.</summary>
    Raster,
}
```

- [ ] **Step 5: Add `IsVisible` and `Opacity` to `ICanvasRaster`**

In `DiffusionNexus.UI/DiffusionCanvas/ICanvasRaster.cs`, insert before the `WorldRect` member:

```csharp
    /// <summary>
    /// Whether the raster is shown. A hidden raster is neither drawn nor fed to the model: what is under
    /// the box is what the model sees, so a layer the user hid must not leak into the input (#594).
    /// </summary>
    /// <remarks>A default member so a raster with no layer state (test stubs, snapshots) is simply shown.</remarks>
    bool IsVisible => true;

    /// <summary>Opacity from 0 to 1, applied on screen and in the composited region alike.</summary>
    double Opacity => 1.0;
```

- [ ] **Step 6: Add the layer properties to `GenerationFrameViewModel`**

In `DiffusionNexus.UI/ViewModels/DiffusionCanvas/GenerationFrameViewModel.cs`:

1. Add `using System.Globalization;` and `using DiffusionNexus.UI.ViewModels;` to the usings.
2. Change the class declaration to:
   `public partial class GenerationFrameViewModel : ObservableObject, ICanvasRaster, ILayerStackItem, IDisposable`
3. Add `[NotifyPropertyChangedFor(nameof(ProvenanceText))]` above each of the existing `_canvasX`, `_canvasY`, `_width`, `_height` and `_seed` fields, under their `[ObservableProperty]`.
4. Add `[NotifyPropertyChangedFor(nameof(Thumbnail))]` under `[ObservableProperty]` on `_frameImage`.
5. Append these members after `IsBusy` / `OnStateChanged`:

```csharp
    // ────────────────────────────── Layer (#594) ──────────────────────────────

    /// <summary>What this layer holds. Always <see cref="CanvasLayerKind.Raster"/> until #595–#597.</summary>
    public CanvasLayerKind Kind => CanvasLayerKind.Raster;

    /// <summary>Layer name shown in the layer stack. Assigned "Layer N" when the candidate is accepted.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>Whether the layer is drawn and fed to the model.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Protects the layer from Delete and Clear canvas. Reorder, rename and hide stay allowed.</summary>
    [ObservableProperty]
    private bool _isLocked;

    private double _opacity = 1.0;

    /// <summary>Opacity from 0 to 1. Out-of-range values clamp; NaN means fully opaque.</summary>
    public double Opacity
    {
        get => _opacity;
        set
        {
            var clamped = double.IsNaN(value) ? 1.0 : Math.Clamp(value, 0.0, 1.0);
            if (SetProperty(ref _opacity, clamped))
            {
                OnPropertyChanged(nameof(OpacityPercent));
                OnPropertyChanged(nameof(OpacityText));
            }
        }
    }

    /// <summary>Opacity as a whole percentage, for the inspector's 0–100 slider.</summary>
    public int OpacityPercent
    {
        get => (int)Math.Round(Opacity * 100);
        set => Opacity = value / 100.0;
    }

    /// <summary>Opacity as display text, formatted invariantly.</summary>
    public string OpacityText => string.Create(CultureInfo.InvariantCulture, $"{OpacityPercent}%");

    /// <summary>The layer row's thumbnail: the raster itself, scaled by the row.</summary>
    public Bitmap? Thumbnail => FrameImage;

    /// <summary>
    /// Seed, size and world position as one line for the inspector. Formatted invariantly in the view
    /// model: XAML <c>StringFormat</c> follows the current culture and renders "1,0" on a German machine.
    /// </summary>
    public string ProvenanceText => string.Create(
        CultureInfo.InvariantCulture,
        $"Seed {(Seed is { } seed ? seed.ToString(CultureInfo.InvariantCulture) : "unknown")} · {Width}×{Height} at ({Math.Round(CanvasX, MidpointRounding.AwayFromZero):0}, {Math.Round(CanvasY, MidpointRounding.AwayFromZero):0})");
```

- [ ] **Step 7: Run the tests and confirm they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~GenerationFrameLayerTests|FullyQualifiedName~DiffusionCanvas" -v q`
Expected: PASS, including every existing `DiffusionCanvas` test. The existing `StubRaster` records still compile because the new `ICanvasRaster` members have defaults.

- [ ] **Step 8: Commit and push**

```bash
git add DiffusionNexus.UI/ViewModels/ILayerStackItem.cs DiffusionNexus.UI/ViewModels/DiffusionCanvas/CanvasLayerKind.cs DiffusionNexus.UI/DiffusionCanvas/ICanvasRaster.cs DiffusionNexus.UI/ViewModels/DiffusionCanvas/GenerationFrameViewModel.cs DiffusionNexus.Tests/DiffusionCanvas/GenerationFrameLayerTests.cs
git diff --cached --numstat; git diff --cached -w --numstat
git commit -m "feat(canvas): layer properties on accepted results (#594)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 2: What the model sees: compositor, hit test, snapshot

**Files:**
- Create: `DiffusionNexus.UI/DiffusionCanvas/CanvasRasterSnapshot.cs`
- Modify: `DiffusionNexus.UI/DiffusionCanvas/CanvasRegionCompositor.cs` (the `CanvasCompositeSource` record, `Composite`'s draw loop, `LoadIntersecting`)
- Modify: `DiffusionNexus.UI/DiffusionCanvas/CanvasRasterHitTest.cs`
- Test: `DiffusionNexus.Tests/DiffusionCanvas/CanvasRegionCompositorTests.cs`, `DiffusionNexus.Tests/DiffusionCanvas/CanvasRasterHitTestTests.cs`, and the new `DiffusionNexus.Tests/DiffusionCanvas/CanvasRasterSnapshotTests.cs`

**Interfaces:**
- Consumes: `ICanvasRaster.IsVisible` and `.Opacity` (Task 1); `GenerationFrameViewModel.Opacity`/`IsVisible` (Task 1)
- Produces:
  - `record CanvasCompositeSource(SKBitmap Bitmap, Rect WorldRect, double Opacity = 1.0)`
  - `static bool CanvasRegionCompositor.IsShown(ICanvasRaster raster)`: `IsVisible && Opacity > 0`
  - `sealed record CanvasRasterSnapshot(double CanvasX, double CanvasY, int Width, int Height, string? ImagePath, bool IsVisible, double Opacity) : ICanvasRaster`, with `static CanvasRasterSnapshot Of(ICanvasRaster raster)`

- [ ] **Step 1: Write the failing tests**

Append to the class in `CanvasRegionCompositorTests.cs`:

```csharp
    [Fact]
    public void ASourceAtHalfOpacity_ReachesTheRegionAtHalfAlpha()
    {
        using var raster = SolidBitmap(64, 64, new SKColor(255, 0, 0));
        var region = new Rect(0, 0, 256, 256);

        using var composite = CanvasRegionCompositor.Composite(
            [new CanvasCompositeSource(raster, region, Opacity: 0.5)], region, 256, 256);

        PixelAt(composite.Bitmap, 128, 128).Alpha.Should().BeCloseTo(128, 2);
        composite.Coverage.Should().Be(1.0, "a half-transparent layer is still something the model sees");
    }

    [Fact]
    public void ASourceAtZeroOpacity_ContributesNothing()
    {
        using var raster = SolidBitmap(64, 64, SKColors.White);
        var region = new Rect(0, 0, 256, 256);

        using var composite = CanvasRegionCompositor.Composite(
            [new CanvasCompositeSource(raster, region, Opacity: 0)], region, 256, 256);

        composite.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void LoadIntersecting_SkipsHiddenAndFullyTransparentRastersWithoutReportingThem()
    {
        var skipped = new List<string>();
        var rasters = new ICanvasRaster[]
        {
            new CanvasRasterSnapshot(0, 0, 512, 512, ImagePath: null, IsVisible: false, Opacity: 1),
            new CanvasRasterSnapshot(0, 0, 512, 512, ImagePath: null, IsVisible: true, Opacity: 0),
        };

        CanvasRegionCompositor.LoadIntersecting(rasters, new Rect(0, 0, 512, 512), skipped.Add)
            .Should().BeEmpty();
        skipped.Should().BeEmpty("a layer the user hid is a choice, not a failure to read one");
    }

    [Fact]
    public void LoadIntersecting_CarriesTheRastersOpacity()
    {
        using var file = new TempCanvasFile(16, 16, SKColors.White);
        var rasters = new ICanvasRaster[] { new CanvasRasterSnapshot(0, 0, 512, 512, file.Path, true, 0.25) };

        var loaded = CanvasRegionCompositor.LoadIntersecting(rasters, new Rect(0, 0, 512, 512));
        try
        {
            loaded.Should().ContainSingle().Which.Opacity.Should().Be(0.25);
        }
        finally
        {
            foreach (var source in loaded)
                source.Bitmap.Dispose();
        }
    }
```

Append to the class in `CanvasRasterHitTestTests.cs`:

```csharp
    [Fact]
    public void HiddenAndFullyTransparentRastersAreNeverHit()
    {
        var below = new CanvasRasterSnapshot(0, 0, 1024, 1024, null, IsVisible: true, Opacity: 1);
        var hiddenAbove = new CanvasRasterSnapshot(0, 0, 1024, 1024, null, IsVisible: false, Opacity: 1);
        var clearAbove = new CanvasRasterSnapshot(0, 0, 1024, 1024, null, IsVisible: true, Opacity: 0);

        CanvasRasterHitTest.TopmostAt([below, hiddenAbove, clearAbove], new Point(10, 10))
            .Should().BeSameAs(below, "right-click acts on what the user can see");
    }
```

Create `DiffusionNexus.Tests/DiffusionCanvas/CanvasRasterSnapshotTests.cs`:

```csharp
using DiffusionNexus.UI.DiffusionCanvas;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// Generate composites off the UI thread. The snapshot is what keeps a mid-run hide or opacity drag
/// from changing the input of a run that already started.
/// </summary>
public class CanvasRasterSnapshotTests
{
    [Fact]
    public void Of_CopiesEveryValueSoLaterChangesDoNotReachIt()
    {
        var frame = new GenerationFrameViewModel
        {
            CanvasX = 64, CanvasY = 128, Width = 512, Height = 256, ImagePath = "a.png", Opacity = 0.5,
        };

        var snapshot = CanvasRasterSnapshot.Of(frame);
        frame.IsVisible = false;
        frame.Opacity = 1;
        frame.CanvasX = 0;

        snapshot.Should().Be(new CanvasRasterSnapshot(64, 128, 512, 256, "a.png", true, 0.5));
        snapshot.FrameImage.Should().BeNull("a snapshot carries no Avalonia bitmap off the UI thread");
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasRegionCompositorTests|FullyQualifiedName~CanvasRasterHitTestTests|FullyQualifiedName~CanvasRasterSnapshotTests" -v q`
Expected: build errors, because `CanvasRasterSnapshot` and the `Opacity` parameter do not exist yet.

- [ ] **Step 3: Add `CanvasRasterSnapshot`**

Create `DiffusionNexus.UI/DiffusionCanvas/CanvasRasterSnapshot.cs`:

```csharp
using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// An immutable copy of a raster's geometry and layer state, taken on the UI thread so region
/// compositing can run on the thread pool without reading live view-model state that the user may be
/// changing (hiding a layer, dragging its opacity) mid-run.
/// </summary>
public sealed record CanvasRasterSnapshot(
    double CanvasX,
    double CanvasY,
    int Width,
    int Height,
    string? ImagePath,
    bool IsVisible,
    double Opacity) : ICanvasRaster
{
    /// <summary>Always null: compositing decodes from <see cref="ImagePath"/>, never from a UI bitmap.</summary>
    public Bitmap? FrameImage => null;

    /// <summary>Copies <paramref name="raster"/>'s current values.</summary>
    public static CanvasRasterSnapshot Of(ICanvasRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        return new(raster.CanvasX, raster.CanvasY, raster.Width, raster.Height,
            raster.ImagePath, raster.IsVisible, raster.Opacity);
    }
}
```

- [ ] **Step 4: Honour opacity and visibility in the compositor**

In `CanvasRegionCompositor.cs`:

1. Replace the record declaration with:

```csharp
/// <summary>One already-decoded raster with the world rectangle it occupies and the opacity it is drawn at.</summary>
public readonly record struct CanvasCompositeSource(SKBitmap Bitmap, Rect WorldRect, double Opacity = 1.0);
```

2. Add this member to the class, after `NeutralFill`:

```csharp
    /// <summary>
    /// Whether a raster is part of what the model sees: visible and not fully transparent. The surface's
    /// hit test, the canvas's region readout and <see cref="LoadIntersecting"/> all use this one rule.
    /// </summary>
    public static bool IsShown(ICanvasRaster raster) => raster.IsVisible && raster.Opacity > 0;
```

3. In `Composite`, change the first `continue` guard in the `foreach` to:
   `if (source.Bitmap is null || source.Bitmap.IsEmpty || source.Opacity <= 0)`
   Then replace `canvas.DrawBitmap(source.Bitmap, src, dest);` with:

```csharp
                    if (source.Opacity >= 1)
                    {
                        canvas.DrawBitmap(source.Bitmap, src, dest);
                    }
                    else
                    {
                        // The paint's alpha scales the bitmap's, the same as the surface's PushOpacity, so a
                        // half-transparent layer reaches the model as it looks on screen.
                        using var paint = new SKPaint
                        {
                            Color = SKColors.White.WithAlpha((byte)Math.Round(255 * source.Opacity)),
                        };
                        canvas.DrawBitmap(source.Bitmap, src, dest, paint);
                    }
```

4. In `LoadIntersecting`, make the first statement inside the `foreach` this guard:

```csharp
                // A hidden or fully transparent layer is the user's choice, not a read failure, so it is
                // skipped without onSkipped: the caller counts skips as a degraded region.
                if (!IsShown(raster))
                    continue;
```

   Then change `loaded.Add(new CanvasCompositeSource(bitmap, rect));` to
   `loaded.Add(new CanvasCompositeSource(bitmap, rect, Math.Clamp(raster.Opacity, 0.0, 1.0)));`

5. Add a sentence to the `<remarks>`/summary of `Composite`: "Each source is drawn at its `Opacity`, and the result is flattened over `NeutralFill` before encoding. A semi-transparent layer therefore reaches the model mixed with grey, while on screen it mixes with the dark canvas background. That small difference is accepted (#594)."

- [ ] **Step 5: Skip hidden rasters in the hit test**

In `CanvasRasterHitTest.TopmostAt`, change the `if` inside the loop to:

```csharp
            if (CanvasRegionCompositor.IsShown(raster) && rect.Width > 0 && rect.Height > 0 && rect.Contains(world))
                hit = raster;
```

Update its summary: "…the topmost **shown** raster containing the point. A hidden or fully transparent layer cannot be right-clicked, because the user cannot see it."

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DiffusionCanvas" -v q`
Expected: PASS.

- [ ] **Step 7: Commit and push**

```bash
git add DiffusionNexus.UI/DiffusionCanvas/ DiffusionNexus.Tests/DiffusionCanvas/
git diff --cached --numstat; git diff --cached -w --numstat
git commit -m "feat(canvas): hidden and translucent layers reach the model as they look (#594)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 3: `CanvasLayerStackViewModel`

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/DiffusionCanvas/CanvasLayerStackViewModel.cs`
- Test: `DiffusionNexus.Tests/DiffusionCanvas/CanvasLayerStackViewModelTests.cs`

**Interfaces:**
- Consumes: `GenerationFrameViewModel` layer properties (Task 1)
- Produces (all on `CanvasLayerStackViewModel`):
  - Constructor `(ObservableCollection<GenerationFrameViewModel> frames, Action<string>? trace = null)`
  - `ObservableCollection<GenerationFrameViewModel> DisplayLayers` (top first), `GenerationFrameViewModel? SelectedLayer`, `bool HasLayers`, `bool HasUnlockedLayers`
  - `IRelayCommand MoveUpCommand`, `MoveDownCommand`, `DeleteSelectedCommand`
  - `static bool CanDelete(GenerationFrameViewModel? layer)`, `bool Delete(GenerationFrameViewModel? layer)`, `(int Removed, int Kept) ClearUnlocked()`, `void AddAccepted(GenerationFrameViewModel frame)`
  - `event EventHandler? LayersChanged`: raised on any collection change and on a change to visibility, opacity or lock

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/DiffusionCanvas/CanvasLayerStackViewModelTests.cs`:

```csharp
using System.Collections.ObjectModel;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>The layer stack's rules (#594): order, selection, lock, and the top-first mirror.</summary>
public class CanvasLayerStackViewModelTests
{
    private readonly ObservableCollection<GenerationFrameViewModel> _frames = [];
    private readonly List<string> _trace = [];

    private CanvasLayerStackViewModel Create() => new(_frames, _trace.Add);

    private static GenerationFrameViewModel Frame(string name) =>
        new() { Name = name, Width = 64, Height = 64, State = GenerationFrameState.Completed };

    [Fact]
    public void DisplayLayers_ListsTheTopLayerFirst_IncludingFramesThatPredateTheStack()
    {
        var a = Frame("a");
        _frames.Add(a);
        var stack = Create();
        var b = Frame("b");
        var c = Frame("c");
        _frames.Add(b);
        _frames.Add(c);

        stack.DisplayLayers.Should().Equal(c, b, a);
        stack.HasLayers.Should().BeTrue();
    }

    [Fact]
    public void DisplayLayers_FollowsInsertRemoveMoveAndReset()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        var c = Frame("c");
        _frames.Add(a);
        _frames.Add(c);
        _frames.Insert(1, b);
        stack.DisplayLayers.Should().Equal(c, b, a);

        _frames.Move(0, 2);                      // a to the top
        stack.DisplayLayers.Should().Equal(a, c, b);

        _frames.Remove(c);
        stack.DisplayLayers.Should().Equal(a, b);

        _frames.Clear();
        stack.DisplayLayers.Should().BeEmpty();
        stack.HasLayers.Should().BeFalse();
    }

    [Fact]
    public void AddAccepted_LandsOnTopEvenWhenALowerLayerIsSelected()
    {
        var stack = Create();
        var lower = Frame("");
        stack.AddAccepted(lower);
        stack.AddAccepted(Frame(""));
        stack.SelectedLayer = lower;

        var accepted = Frame("");
        stack.AddAccepted(accepted);

        _frames[^1].Should().BeSameAs(accepted);
        stack.DisplayLayers[0].Should().BeSameAs(accepted);
        stack.SelectedLayer.Should().BeSameAs(accepted);
        accepted.Name.Should().Be("Layer 3");
    }

    [Fact]
    public void AddAccepted_NeverReusesANumber()
    {
        var stack = Create();
        var first = Frame("");
        stack.AddAccepted(first);
        stack.Delete(first);

        var second = Frame("");
        stack.AddAccepted(second);

        second.Name.Should().Be("Layer 2", "a reused number would make two different layers look the same in the log");
    }

    [Fact]
    public void MoveUp_RaisesTheSelectedLayerOneStepAndStopsAtTheTop()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        _frames.Add(a);
        _frames.Add(b);
        stack.SelectedLayer = a;

        stack.MoveUpCommand.CanExecute(null).Should().BeTrue();
        stack.MoveUpCommand.Execute(null);

        stack.DisplayLayers.Should().Equal(a, b);
        stack.SelectedLayer.Should().BeSameAs(a);
        stack.MoveUpCommand.CanExecute(null).Should().BeFalse("it is already the top layer");
        _trace.Should().Contain(t => t.Contains("Moved layer 'a' up"));
    }

    [Fact]
    public void MoveDown_LowersTheSelectedLayerOneStepAndStopsAtTheBottom()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        _frames.Add(a);
        _frames.Add(b);
        stack.SelectedLayer = b;

        stack.MoveDownCommand.Execute(null);

        stack.DisplayLayers.Should().Equal(a, b);
        stack.MoveDownCommand.CanExecute(null).Should().BeFalse("it is already the bottom layer");
    }

    [Fact]
    public void MoveCommands_AreDisabledWithNoSelection()
    {
        var stack = Create();
        _frames.Add(Frame("a"));
        _frames.Add(Frame("b"));

        stack.MoveUpCommand.CanExecute(null).Should().BeFalse();
        stack.MoveDownCommand.CanExecute(null).Should().BeFalse();
        stack.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Delete_RefusesALockedLayer()
    {
        var stack = Create();
        var locked = Frame("keeper");
        _frames.Add(locked);
        locked.IsLocked = true;
        stack.SelectedLayer = locked;

        stack.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();
        CanvasLayerStackViewModel.CanDelete(locked).Should().BeFalse();
        stack.Delete(locked).Should().BeFalse();

        _frames.Should().Contain(locked);
        _trace.Should().Contain(t => t.StartsWith("Refused to delete layer 'keeper'"));
    }

    [Fact]
    public void LockingTheSelectedLayer_DisablesDeleteImmediately()
    {
        var stack = Create();
        var a = Frame("a");
        _frames.Add(a);
        stack.SelectedLayer = a;
        var notified = false;
        stack.DeleteSelectedCommand.CanExecuteChanged += (_, _) => notified = true;

        a.IsLocked = true;

        notified.Should().BeTrue();
        stack.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void DeleteSelected_SelectsTheLayerThatTookItsPlace()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        var c = Frame("c");
        _frames.Add(a);
        _frames.Add(b);
        _frames.Add(c);                           // display: c, b, a

        stack.SelectedLayer = b;
        stack.DeleteSelectedCommand.Execute(null);
        stack.SelectedLayer.Should().BeSameAs(a, "a moved up into b's row");

        stack.DeleteSelectedCommand.Execute(null);   // a was the bottom row
        stack.SelectedLayer.Should().BeSameAs(c, "with nothing below, the row above is selected");

        stack.DeleteSelectedCommand.Execute(null);
        stack.SelectedLayer.Should().BeNull();
    }

    [Fact]
    public void Delete_OfAnUnselectedLayerKeepsTheSelection()
    {
        var stack = Create();
        var a = Frame("a");
        var b = Frame("b");
        _frames.Add(a);
        _frames.Add(b);
        stack.SelectedLayer = b;

        stack.Delete(a).Should().BeTrue();

        stack.SelectedLayer.Should().BeSameAs(b);
    }

    [Fact]
    public void ClearUnlocked_KeepsLockedLayersAndReportsCounts()
    {
        var stack = Create();
        var keep = Frame("keep");
        _frames.Add(Frame("x"));
        _frames.Add(keep);
        _frames.Add(Frame("y"));
        keep.IsLocked = true;

        var (removed, kept) = stack.ClearUnlocked();

        removed.Should().Be(2);
        kept.Should().Be(1);
        _frames.Should().Equal(keep);
        stack.SelectedLayer.Should().BeSameAs(keep);
        stack.HasUnlockedLayers.Should().BeFalse();
    }

    [Fact]
    public void HasUnlockedLayers_FollowsLockChanges()
    {
        var stack = Create();
        var a = Frame("a");
        _frames.Add(a);
        var raised = false;
        stack.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(CanvasLayerStackViewModel.HasUnlockedLayers);

        a.IsLocked = true;

        raised.Should().BeTrue();
        stack.HasUnlockedLayers.Should().BeFalse();
    }

    [Fact]
    public void LayersChanged_IsRaisedForWhatTheModelSees_NotForARename()
    {
        var stack = Create();
        var a = Frame("a");
        _frames.Add(a);
        var count = 0;
        stack.LayersChanged += (_, _) => count++;

        a.IsVisible = false;
        a.Opacity = 0.5;
        count.Should().Be(2);

        a.Name = "renamed";
        count.Should().Be(2);
        _trace.Should().Contain("Layer 'a' hidden.");
        _trace.Should().Contain("Renamed a layer to 'renamed'.");
    }

    [Fact]
    public void ARemovedLayerIsNoLongerObserved()
    {
        var stack = Create();
        var a = Frame("a");
        _frames.Add(a);
        _frames.Remove(a);
        var count = 0;
        stack.LayersChanged += (_, _) => count++;

        a.IsVisible = false;

        count.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasLayerStackViewModelTests" -v q`
Expected: build error, because `CanvasLayerStackViewModel` does not exist.

- [ ] **Step 3: Implement `CanvasLayerStackViewModel`**

Create `DiffusionNexus.UI/ViewModels/DiffusionCanvas/CanvasLayerStackViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// The canvas's layer stack (#594, #518 region D): ordering, selection, lock and naming rules over the
/// canvas's <c>Frames</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Frames</c> stays the canonical order, <b>bottom to top</b>, because the surface, the hit test and
/// the region compositor all iterate it that way. <see cref="DisplayLayers"/> is a top-first mirror for
/// the layer panel. It is maintained incrementally rather than rebuilt, because clearing a collection a
/// <c>ListBox</c> is bound to writes null back into the selection.
/// </para>
/// <para>
/// Lock protects a layer from removal only: Delete refuses it and <see cref="ClearUnlocked"/> keeps it.
/// </para>
/// </remarks>
public sealed partial class CanvasLayerStackViewModel : ObservableObject
{
    private readonly ObservableCollection<GenerationFrameViewModel> _frames;
    private readonly Action<string> _trace;

    /// <summary>Last number handed out by <see cref="AddAccepted"/>. Never decremented, so a number is never reused.</summary>
    private int _lastNumber;

    public CanvasLayerStackViewModel(ObservableCollection<GenerationFrameViewModel> frames, Action<string>? trace = null)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _trace = trace ?? (_ => { });

        foreach (var frame in _frames)
        {
            DisplayLayers.Insert(0, frame);
            Observe(frame);
        }

        _frames.CollectionChanged += OnFramesChanged;
    }

    /// <summary>
    /// Raised when anything that changes what the model sees or what may be removed changes: the
    /// collection, or a layer's visibility, opacity or lock. Not raised for a rename.
    /// </summary>
    public event EventHandler? LayersChanged;

    /// <summary>The layers top first, as the panel lists them.</summary>
    public ObservableCollection<GenerationFrameViewModel> DisplayLayers { get; } = [];

    /// <summary>The layer the inspector edits and the surface outlines.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private GenerationFrameViewModel? _selectedLayer;

    /// <summary>True when the canvas holds at least one layer.</summary>
    public bool HasLayers => _frames.Count > 0;

    /// <summary>True when Clear canvas would remove something.</summary>
    public bool HasUnlockedLayers => _frames.Any(f => !f.IsLocked);

    /// <summary>Whether <paramref name="layer"/> may be deleted: it exists and is not locked.</summary>
    public static bool CanDelete(GenerationFrameViewModel? layer) => layer is { IsLocked: false };

    /// <summary>Raises the selected layer one step toward the top.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(+1);

    private bool CanMoveUp() =>
        SelectedLayer is { } layer && _frames.IndexOf(layer) is var index && index >= 0 && index < _frames.Count - 1;

    /// <summary>Lowers the selected layer one step toward the bottom.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(-1);

    private bool CanMoveDown() => SelectedLayer is { } layer && _frames.IndexOf(layer) > 0;

    /// <summary>Deletes the selected layer unless it is locked.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected() => Delete(SelectedLayer);

    private bool CanDeleteSelected() => CanDelete(SelectedLayer);

    /// <summary>
    /// Removes and disposes <paramref name="layer"/>. Returns false, and traces why, for a locked layer.
    /// If the deleted layer was selected, the row that takes its place is selected, so the inspector
    /// does not go blank.
    /// </summary>
    public bool Delete(GenerationFrameViewModel? layer)
    {
        if (layer is null)
            return false;

        if (layer.IsLocked)
        {
            _trace($"Refused to delete layer '{layer.Name}': it is locked.");
            return false;
        }

        var wasSelected = ReferenceEquals(SelectedLayer, layer);
        var displayIndex = DisplayLayers.IndexOf(layer);

        // Detach before disposing: a bitmap still bound into the visual tree faults the render.
        _frames.Remove(layer);
        layer.Dispose();

        if (wasSelected)
        {
            SelectedLayer = DisplayLayers.Count == 0
                ? null
                : DisplayLayers[Math.Clamp(displayIndex, 0, DisplayLayers.Count - 1)];
        }

        _trace($"Deleted layer '{layer.Name}' ({_frames.Count} layer(s) left).");
        return true;
    }

    /// <summary>Removes and disposes every unlocked layer; returns how many went and how many stayed.</summary>
    public (int Removed, int Kept) ClearUnlocked()
    {
        var removed = 0;
        foreach (var layer in _frames.Where(f => !f.IsLocked).ToList())
        {
            _frames.Remove(layer);
            layer.Dispose();
            removed++;
        }

        if (SelectedLayer is null || !_frames.Contains(SelectedLayer))
            SelectedLayer = DisplayLayers.FirstOrDefault();

        return (removed, _frames.Count);
    }

    /// <summary>
    /// Puts an accepted candidate on top of the stack and selects it. An unnamed frame is named
    /// "Layer N", where N counts up for the session and is never reused.
    /// </summary>
    public void AddAccepted(GenerationFrameViewModel frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (string.IsNullOrWhiteSpace(frame.Name))
            frame.Name = $"Layer {++_lastNumber}";

        _frames.Add(frame);
        SelectedLayer = frame;
        _trace($"Added layer '{frame.Name}' on top ({_frames.Count} layer(s)).");
    }

    private void Move(int delta)
    {
        if (SelectedLayer is not { } layer)
            return;

        var from = _frames.IndexOf(layer);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _frames.Count)
            return;

        _frames.Move(from, to);

        // A ListBox may treat a move as remove + add and write null back into the selection; restore it.
        SelectedLayer = layer;
        _trace($"Moved layer '{layer.Name}' {(delta > 0 ? "up" : "down")} (now {_frames.Count - to} of {_frames.Count} from the top).");
        NotifyCommands();
    }

    private void OnFramesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // ObservableCollection raises single-item Add/Remove/Replace/Move, and Reset for Clear.
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
            {
                var added = (GenerationFrameViewModel)e.NewItems![0]!;
                // Frames index i maps to display index (count - 1 - i); the display list is one short here.
                DisplayLayers.Insert(DisplayLayers.Count - e.NewStartingIndex, added);
                Observe(added);
                break;
            }
            case NotifyCollectionChangedAction.Remove:
            {
                var removed = (GenerationFrameViewModel)e.OldItems![0]!;
                DisplayLayers.Remove(removed);
                Unobserve(removed);
                break;
            }
            case NotifyCollectionChangedAction.Move:
            {
                var last = _frames.Count - 1;
                DisplayLayers.Move(last - e.OldStartingIndex, last - e.NewStartingIndex);
                break;
            }
            case NotifyCollectionChangedAction.Replace:
            {
                var oldItem = (GenerationFrameViewModel)e.OldItems![0]!;
                var newItem = (GenerationFrameViewModel)e.NewItems![0]!;
                DisplayLayers[DisplayLayers.IndexOf(oldItem)] = newItem;
                Unobserve(oldItem);
                Observe(newItem);
                break;
            }
            default:
            {
                foreach (var layer in DisplayLayers)
                    Unobserve(layer);
                DisplayLayers.Clear();
                foreach (var layer in _frames)
                {
                    DisplayLayers.Insert(0, layer);
                    Observe(layer);
                }

                break;
            }
        }

        if (SelectedLayer is not null && !_frames.Contains(SelectedLayer))
            SelectedLayer = null;

        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(HasUnlockedLayers));
        NotifyCommands();
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Observe(GenerationFrameViewModel layer) => layer.PropertyChanged += OnLayerPropertyChanged;

    private void Unobserve(GenerationFrameViewModel layer) => layer.PropertyChanged -= OnLayerPropertyChanged;

    private void OnLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GenerationFrameViewModel layer)
            return;

        switch (e.PropertyName)
        {
            case nameof(GenerationFrameViewModel.IsVisible):
                _trace($"Layer '{layer.Name}' {(layer.IsVisible ? "shown" : "hidden")}.");
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(GenerationFrameViewModel.Opacity):
                // Not traced: a slider drag raises this dozens of times. The region-composite trace at
                // Generate records what the model actually received.
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(GenerationFrameViewModel.IsLocked):
                _trace($"Layer '{layer.Name}' {(layer.IsLocked ? "locked" : "unlocked")}.");
                OnPropertyChanged(nameof(HasUnlockedLayers));
                DeleteSelectedCommand.NotifyCanExecuteChanged();
                LayersChanged?.Invoke(this, EventArgs.Empty);
                break;

            case nameof(GenerationFrameViewModel.Name):
                _trace($"Renamed a layer to '{layer.Name}'.");
                break;
        }
    }

    private void NotifyCommands()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasLayerStackViewModelTests" -v q`
Expected: PASS (15 tests).

- [ ] **Step 5: Commit and push**

```bash
git add DiffusionNexus.UI/ViewModels/DiffusionCanvas/CanvasLayerStackViewModel.cs DiffusionNexus.Tests/DiffusionCanvas/CanvasLayerStackViewModelTests.cs
git diff --cached --numstat; git diff --cached -w --numstat
git commit -m "feat(canvas): layer stack rules: order, selection, lock (#594)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 4: Wire the stack into the canvas view model

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/DiffusionCanvas/DiffusionCanvasViewModel.cs`
- Test: `DiffusionNexus.Tests/DiffusionCanvas/CanvasLayerStackIntegrationTests.cs`

**Interfaces:**
- Consumes: `CanvasLayerStackViewModel` (Task 3); `CanvasRasterSnapshot.Of` and `CanvasRegionCompositor.IsShown` (Task 2)
- Produces on `DiffusionCanvasViewModel`: `CanvasLayerStackViewModel Layers`, `bool IsLayerPanelVisible` (default true), `ClearCanvasCommand` gated by `Layers.HasUnlockedLayers`, `DeleteFrameCommand` gated by `CanvasLayerStackViewModel.CanDelete`. `ToggleLayerPanelCommand` is **removed**.

- [ ] **Step 1: Write the failing tests**

Create `DiffusionNexus.Tests/DiffusionCanvas/CanvasLayerStackIntegrationTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>The layer stack inside the canvas: what the model sees follows what the user shows (#594).</summary>
public class CanvasLayerStackIntegrationTests
{
    private static DiffusionCanvasViewModel Canvas(FakeDiffusionBackend backend)
    {
        var vm = new DiffusionCanvasViewModel(backend) { PromptText = "a lighthouse at dusk" };
        vm.BitmapDecoder = _ =>
        {
            var sentinel = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
            GC.SuppressFinalize(sentinel);
            return sentinel;
        };
        vm.OutputsWriter = (bytes, seed) => $"C:\\fake-outputs\\{seed}-{bytes.Length}.png";
        return vm;
    }

    [Fact]
    public async Task AcceptingACandidate_AddsANamedSelectedTopLayer()
    {
        var vm = Canvas(new FakeDiffusionBackend());
        vm.BatchCount = 2;
        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Staging.AcceptAllCommand.Execute(null);

        vm.Layers.DisplayLayers.Select(l => l.Name).Should().Equal("Layer 2", "Layer 1");
        vm.Layers.SelectedLayer.Should().BeSameAs(vm.Frames[^1]);
    }

    [Fact]
    public void HidingTheLayerUnderTheBox_TurnsTheReadoutBackToTextToImage()
    {
        var vm = new DiffusionCanvasViewModel();
        vm.Box.SetSize(512, 512);
        vm.Box.SetPosition(0, 0);
        var frame = new GenerationFrameViewModel { Width = 512, Height = 512, ImagePath = "x.png" };
        vm.Frames.Add(frame);
        vm.IsRegionOccupied.Should().BeTrue();

        frame.IsVisible = false;
        vm.IsRegionOccupied.Should().BeFalse("a hidden layer is not what the model sees");
        vm.RegionModeText.Should().Contain("Text to image");

        frame.IsVisible = true;
        frame.Opacity = 0;
        vm.IsRegionOccupied.Should().BeFalse("a fully transparent layer is not either");

        frame.Opacity = 0.4;
        vm.IsRegionOccupied.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_OverAHiddenLayerSendsNoInitImage()
    {
        using var canvas = new TempCanvasFile(512, 512, SKColors.White);
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(512, 512);
        vm.Box.SetPosition(0, 0);
        var frame = canvas.AsFrame(0, 0, 512, 512);
        vm.Frames.Add(frame);
        frame.IsVisible = false;

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.InitImage.Should().BeNull("the only layer under the box is hidden");
    }

    [Fact]
    public void ClearCanvas_KeepsLockedLayersAndIsDisabledWhenEverythingIsLocked()
    {
        var vm = new DiffusionCanvasViewModel();
        var keep = new GenerationFrameViewModel { Name = "keep" };
        vm.Frames.Add(keep);
        vm.Frames.Add(new GenerationFrameViewModel { Name = "gone" });
        keep.IsLocked = true;

        vm.ClearCanvasCommand.CanExecute(null).Should().BeTrue();
        vm.ClearCanvasCommand.Execute(null);

        vm.Frames.Should().Equal(keep);
        vm.ClearCanvasCommand.CanExecute(null).Should().BeFalse("only a locked layer is left");
    }

    [Fact]
    public void DeleteFrameCommand_CannotExecuteForALockedLayer()
    {
        var vm = new DiffusionCanvasViewModel();
        var frame = new GenerationFrameViewModel { Name = "keep", IsLocked = true };
        vm.Frames.Add(frame);

        vm.DeleteFrameCommand!.CanExecute(frame).Should().BeFalse();
        vm.DeleteFrameCommand.Execute(frame);

        vm.Frames.Should().Contain(frame);
    }

    [Fact]
    public void TheLayerPanelIsShownByDefault()
    {
        new DiffusionCanvasViewModel().IsLayerPanelVisible.Should().BeTrue();
    }
}
```

`FakeDiffusionBackend` and `TempCanvasFile` already exist in `CanvasTestDoubles.cs`.

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasLayerStackIntegrationTests" -v q`
Expected: build errors, because `Layers` and `IsLayerPanelVisible` do not exist.

- [ ] **Step 3: Add `Layers` and `IsLayerPanelVisible`**

In `DiffusionCanvasViewModel.cs`, after the `Staging` property:

```csharp
    /// <summary>The layer stack over <see cref="Frames"/>: order, selection, lock (#594).</summary>
    public CanvasLayerStackViewModel Layers { get; }

    /// <summary>Whether the right-hand layer column is shown. Toggled by the tool strip's Layers button.</summary>
    [ObservableProperty]
    private bool _isLayerPanelVisible = true;
```

In **all three** constructors (the parameterless one, the `LocalDiffusionBackendProvider` one and the `internal` engine-only one), add this line directly above the existing `DeleteFrameCommand = …` line, and replace that line:

```csharp
        Layers = new CanvasLayerStackViewModel(Frames, EmitInfo);
        DeleteFrameCommand = new RelayCommand<GenerationFrameViewModel?>(DeleteFrame, CanvasLayerStackViewModel.CanDelete);
```

- [ ] **Step 4: Route events, delete, clear and accept through the stack**

1. In `WireCanvasEvents`, replace `Frames.CollectionChanged += (_, _) => RefreshRegionMode();` with:

```csharp
        // The stack raises LayersChanged for collection changes as well as visibility, opacity and lock,
        // so the readout and the removal commands follow both.
        Layers.LayersChanged += (_, _) =>
        {
            RefreshRegionMode();
            ClearCanvasCommand.NotifyCanExecuteChanged();
            DeleteFrameCommand?.NotifyCanExecuteChanged();
        };
```

2. Replace the body of `DeleteFrame` with `Layers.Delete(frame);` (the stack traces the outcome). Update its summary: "Right-click on a result → Delete. A locked layer is refused; the flyout item is disabled for it."

3. Replace `ClearCanvas` with:

```csharp
    /// <summary>Removes every unlocked layer from the canvas, releasing their bitmaps. Locked layers stay.</summary>
    [RelayCommand(CanExecute = nameof(CanClearCanvas))]
    private void ClearCanvas()
    {
        var (removed, kept) = Layers.ClearUnlocked();
        EmitInfo(kept == 0
            ? $"Cleared the canvas ({removed} result(s) removed)."
            : $"Cleared the canvas ({removed} result(s) removed, {kept} locked layer(s) kept).");
    }

    private bool CanClearCanvas() => Layers.HasUnlockedLayers;
```

4. In `OnCandidateAccepted`, replace `Frames.Add(frame);` with `Layers.AddAccepted(frame);`.

5. Replace `CanContribute` with:

```csharp
    /// <summary>
    /// True when the raster is shown (visible, not fully transparent) and has a saved file the compositor
    /// could read the region back from. The same rule decides the readout and what Generate composites.
    /// </summary>
    private static bool CanContribute(ICanvasRaster raster) =>
        CanvasRegionCompositor.IsShown(raster) && !string.IsNullOrWhiteSpace(raster.ImagePath);
```

6. In the Generate path, replace `var rasters = Frames.Cast<ICanvasRaster>().ToArray();` with:

```csharp
            var rasters = Frames.Select(f => (ICanvasRaster)CanvasRasterSnapshot.Of(f)).ToArray();
```

   Extend the comment above it: "…and the snapshot copies visibility and opacity, so hiding a layer or dragging its opacity mid-run cannot change this run's input."

7. Delete the `ToggleLayerPanel` placeholder: its `// TODO(v2-layers)` comment, `[RelayCommand(CanExecute = nameof(AlwaysFalse))]` and the method. Keep `AlwaysFalse`, since the other placeholders still use it.

8. In the `<remarks>` on `BuildRegionInitImage`, change "True masked outpainting needs the layer stack (issue #518 region D)" to "True masked outpainting needs the inpaint mask layer (#595)".

- [ ] **Step 5: Run the canvas tests and confirm they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DiffusionCanvas" -v q`
Expected: PASS. If an existing test asserts the old "Cleared the canvas" text or executes `ClearCanvasCommand` on an empty canvas and expects a log line, update it to the new behaviour (disabled with no layers) and record that in the commit message.

- [ ] **Step 6: Build the UI project**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -v q`
Expected: the build fails in `DiffusionCanvasView.axaml`, which still binds `ToggleLayerPanelCommand`. Change that `Button` to the toggle Task 7 specifies now, so the build is green at this commit:

```xml
              <ToggleButton Content="Layers" IsChecked="{Binding IsLayerPanelVisible}"
                            ToolTip.Tip="Show or hide the layer stack" />
```

Re-run the build. Expected: success.

- [ ] **Step 7: Commit and push**

```bash
git add DiffusionNexus.UI/ViewModels/DiffusionCanvas/DiffusionCanvasViewModel.cs DiffusionNexus.UI/Views/DiffusionCanvas/DiffusionCanvasView.axaml DiffusionNexus.Tests/DiffusionCanvas/
git diff --cached --numstat; git diff --cached -w --numstat
git commit -m "feat(canvas): canvas runs on the layer stack; hidden layers stay out of img2img (#594)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 5: Surface draws visibility, opacity and the selection

**Files:**
- Modify: `DiffusionNexus.UI/Views/Controls/DiffusionCanvasSurface.cs`
- Modify: `DiffusionNexus.UI/REUSABLES.md` (the `DiffusionCanvasSurface` row)

**Interfaces:**
- Consumes: `ICanvasRaster.IsVisible`/`Opacity` (Task 1); `ILayerStackItem.IsLocked` (Task 1)
- Produces: the `DiffusionCanvasSurface.SelectedRaster` styled property (`object?`)

This task is rendering only. No unit test can observe a `DrawingContext` here, so it is verified by build plus the GUI smoke in Task 9.

- [ ] **Step 1: Add the `SelectedRaster` property and its pen**

Add `using DiffusionNexus.UI.ViewModels;` to the usings. Next to `RasterOutline`, add:

```csharp
    private static readonly IPen SelectedRasterOutline = new Pen(new SolidColorBrush(Color.Parse("#3D8BFD")), 2);
```

After the `DeleteRasterCommand` property block, add:

```csharp
    public static readonly StyledProperty<object?> SelectedRasterProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, object?>(nameof(SelectedRaster));

    /// <summary>
    /// The layer selected in the layer panel. It is outlined in solid accent blue, distinct from the
    /// box's marching ants, so the user can see which raster the inspector is editing.
    /// </summary>
    public object? SelectedRaster
    {
        get => GetValue(SelectedRasterProperty);
        set => SetValue(SelectedRasterProperty, value);
    }
```

In `OnPropertyChanged`, add `|| change.Property == SelectedRasterProperty` to the `InvalidateVisual()` group.

- [ ] **Step 2: Draw with visibility and opacity, then the selection**

Replace `DrawRasters` with:

```csharp
    private void DrawRasters(DrawingContext context, Rect bounds)
    {
        foreach (var raster in EnumerateRasters())
        {
            // Hidden means hidden: no pixels and no outline. The compositor applies the same rule, so the
            // canvas never shows something the model will not get, or the reverse.
            if (!raster.IsVisible)
                continue;

            var screen = Viewport.WorldToScreen(raster.WorldRect);
            if (!screen.Intersects(bounds))
                continue;

            using (context.PushOpacity(Math.Clamp(raster.Opacity, 0.0, 1.0)))
            {
                if (raster.FrameImage is { } image)
                    context.DrawImage(image, new Rect(image.Size), screen);
                else
                    context.FillRectangle(RasterPlaceholder, screen);
            }

            context.DrawRectangle(null, RasterOutline, screen);
        }

        // Outlined even when hidden, so a selected hidden layer can still be found on the canvas.
        if (SelectedRaster is ICanvasRaster selected)
        {
            var screen = Viewport.WorldToScreen(selected.WorldRect);
            if (screen.Intersects(bounds))
                context.DrawRectangle(null, SelectedRasterOutline, screen.Inflate(1));
        }
    }
```

- [ ] **Step 3: Say why Delete is disabled on a locked raster**

In `ShowRasterFlyout`, replace the `MenuItem` initializer's `Header` line with:

```csharp
            // The command refuses a locked layer, so the item renders disabled. The reason goes in the
            // header because Avalonia does not show tooltips on disabled items by default.
            Header = raster is ILayerStackItem { IsLocked: true }
                ? "Delete result (locked: unlock it in the layer panel first)"
                : "Delete result",
```

- [ ] **Step 4: Update the REUSABLES row**

In `DiffusionNexus.UI/REUSABLES.md`, append to the `DiffusionCanvasSurface` row's description: " Draws each raster's `IsVisible`/`Opacity` and outlines `SelectedRaster` (the layer panel's selection)."

- [ ] **Step 5: Build**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -v q`
Expected: success, 0 errors.

- [ ] **Step 6: Commit and push**

```bash
git add DiffusionNexus.UI/Views/Controls/DiffusionCanvasSurface.cs DiffusionNexus.UI/REUSABLES.md
git diff --cached --numstat; git diff --cached -w --numstat
git commit -m "feat(canvas): surface draws layer visibility, opacity and the selected layer (#594)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 6: The shared `LayerStackPanel` control

**Files:**
- Create: `DiffusionNexus.UI/Views/Controls/LayerStackPanel.axaml`
- Create: `DiffusionNexus.UI/Views/Controls/LayerStackPanel.axaml.cs` (a plain `UserControl`, like `SearchableBaseModelPicker`; no existing control derives from `ControlBase`, and this one needs no injected services)
- Modify: `DiffusionNexus.UI/REUSABLES.md` (new row in section 2)

**Interfaces:**
- Consumes: `ILayerStackItem` and `LayerStackNaming.Resolve` (Task 1)
- Produces: `LayerStackPanel` with styled properties `Items` (`IEnumerable?`), `SelectedItem` (`object?`, two-way by default), `MoveUpCommand`, `MoveDownCommand`, `DeleteCommand` (`ICommand?`), `LeadingTools` (`object?`), `LockToolTip` (`string`), `ListMaxHeight` (`double`, default `double.PositiveInfinity`)

The rename rules are already unit-tested through `LayerStackNaming` (Task 1). The control's UI is verified in Task 9's smoke.

- [ ] **Step 1: Write the XAML**

Create `DiffusionNexus.UI/Views/Controls/LayerStackPanel.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="using:DiffusionNexus.UI.Views.Controls"
             xmlns:vm="using:DiffusionNexus.UI.ViewModels"
             x:Class="DiffusionNexus.UI.Views.Controls.LayerStackPanel">

  <UserControl.Styles>
    <Style Selector="ListBox#LayerList > ListBoxItem">
      <Setter Property="Padding" Value="2,1" />
    </Style>
  </UserControl.Styles>

  <!-- Host buttons first, then the shared reorder/delete trio. The host owns every command, so each
       screen keeps its own rules (a locked layer cannot be deleted; the editor keeps its last layer). -->
  <DockPanel>
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="2" Margin="0,0,0,6">
      <ContentControl Content="{Binding $parent[local:LayerStackPanel].LeadingTools}" />
      <Button Content="&#x2191;" Command="{Binding $parent[local:LayerStackPanel].MoveUpCommand}"
              Padding="6,2" FontSize="12" ToolTip.Tip="Move the layer up" />
      <Button Content="&#x2193;" Command="{Binding $parent[local:LayerStackPanel].MoveDownCommand}"
              Padding="6,2" FontSize="12" ToolTip.Tip="Move the layer down" />
      <Button Content="&#x2212;" Command="{Binding $parent[local:LayerStackPanel].DeleteCommand}"
              Padding="6,2" FontSize="12" FontWeight="Bold"
              ToolTip.Tip="Delete the layer. A locked layer cannot be deleted." />
    </StackPanel>

    <ListBox x:Name="LayerList"
             ItemsSource="{Binding $parent[local:LayerStackPanel].Items}"
             SelectedItem="{Binding $parent[local:LayerStackPanel].SelectedItem, Mode=TwoWay}"
             MaxHeight="{Binding $parent[local:LayerStackPanel].ListMaxHeight}"
             SelectionMode="Single"
             Background="#252525" CornerRadius="2" MinHeight="60"
             ScrollViewer.HorizontalScrollBarVisibility="Disabled">
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="vm:ILayerStackItem">
          <Grid ColumnDefinitions="Auto,Auto,*,Auto,Auto" ColumnSpacing="4" Height="36">
            <ToggleButton Grid.Column="0" IsChecked="{Binding IsVisible, Mode=TwoWay}"
                          Padding="4,2" VerticalAlignment="Center" ToolTip.Tip="Show or hide the layer">
              <TextBlock Text="&#x1F441;" FontSize="10" />
            </ToggleButton>

            <Border Grid.Column="1" Width="28" Height="28" CornerRadius="2" Background="#333" ClipToBounds="True">
              <Image Source="{Binding Thumbnail}" Stretch="Uniform" />
            </Border>

            <!-- Name and its in-place editor share one cell. Double-click swaps them (code-behind). -->
            <Panel Grid.Column="2" VerticalAlignment="Center">
              <TextBlock Classes="layerName" Text="{Binding Name}" FontSize="11"
                         TextTrimming="CharacterEllipsis" ToolTip.Tip="Double-click to rename"
                         DoubleTapped="OnNameDoubleTapped" />
              <TextBox Classes="layerRename" IsVisible="False" FontSize="11" Padding="2,0" MinHeight="0"
                       MaxLength="200" LostFocus="OnRenameLostFocus" />
            </Panel>

            <ToggleButton Grid.Column="3" IsChecked="{Binding IsLocked, Mode=TwoWay}"
                          Padding="4,2" VerticalAlignment="Center"
                          ToolTip.Tip="{Binding $parent[local:LayerStackPanel].LockToolTip}">
              <TextBlock Text="&#x1F512;" FontSize="10" />
            </ToggleButton>

            <TextBlock Grid.Column="4" Text="{Binding OpacityText}" FontSize="9" Opacity="0.6"
                       VerticalAlignment="Center" MinWidth="28" TextAlignment="Right" />
          </Grid>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</UserControl>
```

- [ ] **Step 2: Write the code-behind**

Create `DiffusionNexus.UI/Views/Controls/LayerStackPanel.axaml.cs`:

```csharp
using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// A layer list: each row has a show/hide toggle, a thumbnail, the name (double-click to rename), a lock
/// toggle and the opacity, under a ↑/↓/− toolbar. Shared by the Image Editor and the Diffusion Canvas.
/// </summary>
/// <remarks>
/// <para>
/// The host supplies <see cref="Items"/> already in display order (top layer first) and owns every
/// command. The control never reorders anything itself.
/// </para>
/// <para>
/// Rename is UI state local to this control. Only the committed name reaches the row, through
/// <see cref="LayerStackNaming.Resolve"/>, so a blank name never lands.
/// </para>
/// </remarks>
public partial class LayerStackPanel : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<LayerStackPanel, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<LayerStackPanel, object?>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> MoveUpCommandProperty =
        AvaloniaProperty.Register<LayerStackPanel, ICommand?>(nameof(MoveUpCommand));

    public static readonly StyledProperty<ICommand?> MoveDownCommandProperty =
        AvaloniaProperty.Register<LayerStackPanel, ICommand?>(nameof(MoveDownCommand));

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<LayerStackPanel, ICommand?>(nameof(DeleteCommand));

    public static readonly StyledProperty<object?> LeadingToolsProperty =
        AvaloniaProperty.Register<LayerStackPanel, object?>(nameof(LeadingTools));

    public static readonly StyledProperty<string> LockToolTipProperty =
        AvaloniaProperty.Register<LayerStackPanel, string>(nameof(LockToolTip),
            defaultValue: "Lock the layer so it cannot be deleted");

    public static readonly StyledProperty<double> ListMaxHeightProperty =
        AvaloniaProperty.Register<LayerStackPanel, double>(nameof(ListMaxHeight), defaultValue: double.PositiveInfinity);

    public LayerStackPanel()
    {
        InitializeComponent();

        // Tunnel, so Enter and Escape reach us even if the TextBox would mark them handled.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Rows implementing <see cref="ILayerStackItem"/>, top layer first.</summary>
    public IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    /// <summary>The selected row. Two-way by default.</summary>
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    /// <summary>Moves the selected layer toward the top. Owned by the host.</summary>
    public ICommand? MoveUpCommand { get => GetValue(MoveUpCommandProperty); set => SetValue(MoveUpCommandProperty, value); }

    /// <summary>Moves the selected layer toward the bottom. Owned by the host.</summary>
    public ICommand? MoveDownCommand { get => GetValue(MoveDownCommandProperty); set => SetValue(MoveDownCommandProperty, value); }

    /// <summary>Deletes the selected layer. Owned by the host, which refuses a locked layer.</summary>
    public ICommand? DeleteCommand { get => GetValue(DeleteCommandProperty); set => SetValue(DeleteCommandProperty, value); }

    /// <summary>Host-specific buttons shown before ↑/↓/− (the editor's Add and Duplicate).</summary>
    public object? LeadingTools { get => GetValue(LeadingToolsProperty); set => SetValue(LeadingToolsProperty, value); }

    /// <summary>The lock toggle's tooltip: what lock means on this screen.</summary>
    public string LockToolTip { get => GetValue(LockToolTipProperty); set => SetValue(LockToolTipProperty, value); }

    /// <summary>Caps the list's height; unbounded by default so a docked panel can fill its column.</summary>
    public double ListMaxHeight { get => GetValue(ListMaxHeightProperty); set => SetValue(ListMaxHeightProperty, value); }

    private void OnNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock label || label.DataContext is not ILayerStackItem item || label.Parent is not Panel cell)
            return;

        var editor = cell.Children.OfType<TextBox>().FirstOrDefault();
        if (editor is null)
            return;

        editor.Text = item.Name;
        label.IsVisible = false;
        editor.IsVisible = true;
        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { IsVisible: true } editor || !editor.Classes.Contains("layerRename"))
            return;

        if (e.Key == Key.Enter)
        {
            EndRename(editor, commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndRename(editor, commit: false);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } editor)
            EndRename(editor, commit: true);
    }

    private void EndRename(TextBox editor, bool commit)
    {
        if (!editor.IsVisible)
            return;

        // Hide first: moving focus away below raises LostFocus, which must find nothing left to commit.
        editor.IsVisible = false;
        if (editor.Parent is Panel cell)
        {
            foreach (var label in cell.Children.OfType<TextBlock>())
                label.IsVisible = true;
        }

        if (commit && editor.DataContext is ILayerStackItem item)
        {
            var name = LayerStackNaming.Resolve(editor.Text, item.Name);
            if (!string.Equals(name, item.Name, StringComparison.Ordinal))
                item.Name = name;
        }

        LayerList.Focus();
    }
}
```

If the build reports the generated field is not called `LayerList`, use `this.FindControl<ListBox>("LayerList")`. The repo's other controls rely on `x:Name` field generation, so the field is expected to exist.

- [ ] **Step 3: Add the REUSABLES row**

In `DiffusionNexus.UI/REUSABLES.md` section 2, add this row directly after the `DiffusionCanvasSurface` row:

```markdown
| `LayerStackPanel` (+ `ILayerStackItem`, `LayerStackNaming`) | [Views/Controls/LayerStackPanel.axaml](Views/Controls/LayerStackPanel.axaml) — layer list: per-row show/hide, thumbnail, double-click rename, lock, opacity text, under a ↑/↓/− toolbar with a `LeadingTools` slot for host buttons. The host supplies rows top-first (`Items`, `SelectedItem`) and owns `MoveUpCommand`/`MoveDownCommand`/`DeleteCommand`; renames commit through `LayerStackNaming.Resolve`. Used by the Image Editor and the Diffusion Canvas. |
```

- [ ] **Step 4: Build**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -v q`
Expected: success, 0 errors. A compiled-binding error on `$parent[local:LayerStackPanel]` means the `local` xmlns is wrong; it must be `using:DiffusionNexus.UI.Views.Controls`.

- [ ] **Step 5: Commit and push**

```bash
git add DiffusionNexus.UI/Views/Controls/LayerStackPanel.axaml DiffusionNexus.UI/Views/Controls/LayerStackPanel.axaml.cs DiffusionNexus.UI/REUSABLES.md
git diff --cached --numstat; git diff --cached -w --numstat
git commit -m "feat(ui): shared LayerStackPanel control (#594)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 7: The canvas's layer column and inspector

**Files:**
- Modify: `DiffusionNexus.UI/Views/DiffusionCanvas/DiffusionCanvasView.axaml`

**Interfaces:**
- Consumes: `LayerStackPanel` (Task 6); `Layers.*`, `IsLayerPanelVisible` (Task 4); `SelectedRaster` (Task 5); `ProvenanceText`, `OpacityPercent`, `OpacityText`, `IsLocked`, `Prompt` (Task 1)

- [ ] **Step 1: Add the third column**

Change `<Grid Grid.Row="1" ColumnDefinitions="340,*">` to `<Grid Grid.Row="1" ColumnDefinitions="340,*,Auto">`.

- [ ] **Step 2: Bind the surface to the selection**

On `<controls:DiffusionCanvasSurface …>`, add `SelectedRaster="{Binding Layers.SelectedLayer}"`.

- [ ] **Step 3: Add the layer column after the canvas column's closing `</Grid>`**

Insert immediately before the outer `</Grid>` that closes `Grid.Row="1"`:

```xml
      <!-- ═══════════════════════ Layer column (#594) ═══════════════════════ -->
      <Border Grid.Column="2" Name="LayerColumn" Width="280"
              Background="#1E1E1E" BorderBrush="#333" BorderThickness="1,0,0,0" Padding="10"
              IsVisible="{Binding IsLayerPanelVisible}">
        <DockPanel>
          <TextBlock DockPanel.Dock="Top" Text="Layers" FontWeight="SemiBold" FontSize="14" Margin="0,0,0,8" />

          <!-- Inspector, docked to the bottom so the list fills the height between. It swaps with the
               selection rather than opening a dialog, so adjusting opacity never covers the image. -->
          <Border DockPanel.Dock="Bottom" BorderBrush="#444" BorderThickness="0,1,0,0"
                  Padding="0,8,0,0" Margin="0,8,0,0"
                  IsVisible="{Binding Layers.SelectedLayer, Converter={x:Static ObjectConverters.IsNotNull}}">
            <StackPanel Spacing="6">
              <TextBlock Text="Opacity" FontSize="11" Opacity="0.7" />
              <Grid ColumnDefinitions="*,40">
                <Slider Grid.Column="0" Minimum="0" Maximum="100" TickFrequency="1" IsSnapToTickEnabled="True"
                        Value="{Binding Layers.SelectedLayer.OpacityPercent}" />
                <TextBlock Grid.Column="1" Text="{Binding Layers.SelectedLayer.OpacityText}" FontSize="10"
                           VerticalAlignment="Center" HorizontalAlignment="Right" Opacity="0.7" />
              </Grid>
              <CheckBox Content="Locked: cannot be deleted or cleared"
                        IsChecked="{Binding Layers.SelectedLayer.IsLocked}" />
              <TextBlock Text="Prompt" FontSize="11" Opacity="0.7" Margin="0,4,0,0" />
              <ScrollViewer MaxHeight="90" HorizontalScrollBarVisibility="Disabled">
                <SelectableTextBlock Text="{Binding Layers.SelectedLayer.Prompt}" TextWrapping="Wrap" FontSize="11" />
              </ScrollViewer>
              <TextBlock Text="{Binding Layers.SelectedLayer.ProvenanceText}" FontSize="10" Opacity="0.7"
                         TextWrapping="Wrap" />
            </StackPanel>
          </Border>

          <TextBlock DockPanel.Dock="Top" Text="Accepted results appear here as layers."
                     Foreground="#888" FontSize="11" TextWrapping="Wrap" Margin="0,0,0,6"
                     IsVisible="{Binding !Layers.HasLayers}" />

          <controls:LayerStackPanel Items="{Binding Layers.DisplayLayers}"
                                    SelectedItem="{Binding Layers.SelectedLayer, Mode=TwoWay}"
                                    MoveUpCommand="{Binding Layers.MoveUpCommand}"
                                    MoveDownCommand="{Binding Layers.MoveDownCommand}"
                                    DeleteCommand="{Binding Layers.DeleteSelectedCommand}"
                                    LockToolTip="Lock the layer: it cannot be deleted, and Clear canvas keeps it" />
        </DockPanel>
      </Border>
```

- [ ] **Step 4: Update the Clear canvas tooltip**

Change the Clear canvas button's tooltip to `"Remove every unlocked layer from the canvas. Locked layers stay."`

- [ ] **Step 5: Build**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -v q`
Expected: success. If the compiled binding rejects `SelectedItem="{Binding Layers.SelectedLayer, Mode=TwoWay}"` (`object?` → `GenerationFrameViewModel?`), keep the binding and confirm in the smoke that selecting a row updates the inspector. Avalonia converts a reference of the right runtime type.

- [ ] **Step 6: Commit and push**

```bash
git add DiffusionNexus.UI/Views/DiffusionCanvas/DiffusionCanvasView.axaml
git diff --cached --numstat; git diff --cached -w --numstat
git commit -m "feat(canvas): layer column with inspector (#594)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 8: Move the Image Editor onto `LayerStackPanel`

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/LayerViewModel.cs` (implements `ILayerStackItem`)
- Modify: `DiffusionNexus.UI/ViewModels/LayerPanelViewModel.cs` (sync guard, lock blocks delete, trace)
- Modify: `DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs:432` (pass the trace)
- Modify: `DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml:274-320` (swap the inline list)
- Test: `DiffusionNexus.Tests/ViewModels/LayerPanelViewModelTests.cs`

**Interfaces:**
- Consumes: `ILayerStackItem` (Task 1), `LayerStackPanel` (Task 6)
- Produces: the constructor `LayerPanelViewModel(Func<bool> hasImage, Action<string>? trace = null)`

- [ ] **Step 1: Write the failing tests**

Append to `LayerPanelViewModelTests` (it already has `using DiffusionNexus.UI.ImageEditor;`):

```csharp
    #region Layer stack panel (#594)

    [Fact]
    public void DeleteIsDisabledForALockedLayer()
    {
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        stack.AddLayer("Top");
        _sut.SyncLayers(stack);
        _sut.SelectedLayer = _sut.Layers[0];
        _sut.DeleteLayerCommand.CanExecute(null).Should().BeTrue();

        _sut.Layers[0].IsLocked = true;

        _sut.DeleteLayerCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void SyncLayers_IgnoresTheListBoxNullWriteBackAndKeepsTheActiveLayer()
    {
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Bottom");
        var top = stack.AddLayer("Top");
        stack.AddLayer("Third");
        stack.ActiveLayer = top;
        var raisedNull = false;
        _sut.LayerSelectionChanged += (_, layer) =>
        {
            if (layer is null)
            {
                raisedNull = true;
                stack.ActiveLayer = null;   // what ImageEditView's handler does with a null selection
            }
        };

        // Mimic the ListBox: when its ItemsSource is cleared it writes null into SelectedItem.
        _sut.Layers.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                _sut.SelectedLayer = null;
        };

        _sut.SyncLayers(stack);

        raisedNull.Should().BeFalse("a sync is not the user clearing the selection");
        _sut.SelectedLayer!.Layer.Should().BeSameAs(top);
    }

    [Fact]
    public void SyncLayers_OfAnEmptyStackClearsTheSelection()
    {
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Only");
        _sut.SyncLayers(stack);

        _sut.SyncLayers(null);

        _sut.SelectedLayer.Should().BeNull();
    }

    [Fact]
    public void LockAndRenameAreTracedOnce()
    {
        var trace = new List<string>();
        var sut = new LayerPanelViewModel(hasImage: () => true, trace: trace.Add);
        var stack = new LayerStack(10, 10);
        stack.AddLayer("Sky");
        sut.SyncLayers(stack);

        sut.Layers[0].IsLocked = true;
        sut.Layers[0].Name = "Clouds";

        trace.Should().Equal("Layer 'Sky' locked.", "Renamed a layer to 'Clouds'.");
    }

    #endregion
```

If `LayerStack(int, int)` needs anything else to accept `AddLayer`, follow how existing `LayerStack` tests build one (`grep -rn "new LayerStack(" DiffusionNexus.Tests`).

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LayerPanelViewModelTests" -v q`
Expected: build error, because the `trace` parameter does not exist yet.

- [ ] **Step 3: `LayerViewModel` implements `ILayerStackItem`**

Change its declaration from `public partial class LayerViewModel : ObservableObject` to `public partial class LayerViewModel : ObservableObject, ILayerStackItem`. Every member the interface needs already exists with a matching signature (`Name`, `IsVisible`, `IsLocked` get/set; `OpacityText`, `Thumbnail` get).

- [ ] **Step 4: Guard, lock rule and trace in `LayerPanelViewModel`**

1. Add `using System.ComponentModel;`. Add fields:

```csharp
    private readonly Action<string>? _trace;

    /// <summary>
    /// True while <see cref="SyncLayers"/> rebuilds <see cref="Layers"/>. The panel's ListBox writes null
    /// into <see cref="SelectedLayer"/> when its items are cleared; accepting that mid-sync would clear
    /// the editor core's active layer before the sync reads it.
    /// </summary>
    private bool _isSyncing;

    /// <summary>Last traced (name, locked) per row. LayerViewModel raises each change twice (its own setter
    /// plus the forwarded Layer event), so the trace compares against this to log once.</summary>
    private readonly Dictionary<LayerViewModel, (string Name, bool Locked)> _traced = [];
```

2. Change the constructor signature to `public LayerPanelViewModel(Func<bool> hasImage, Action<string>? trace = null)`, assign `_trace = trace;`, and change the `DeleteLayerCommand` predicate to:
   `() => _hasImage() && SelectedLayer is not null && Layers.Count > 1 && !SelectedLayer.Layer.IsInpaintMask && !SelectedLayer.IsLocked`

3. In the `SelectedLayer` setter, make the first statement:

```csharp
            if (_isSyncing && value is null)
                return;
```

4. Replace `SyncLayers` with:

```csharp
    public void SyncLayers(LayerStack? layerStack)
    {
        // Read before clearing: clearing can trigger a null selection write-back (see _isSyncing).
        var active = layerStack?.ActiveLayer;
        LayerViewModel? target = null;

        _isSyncing = true;
        try
        {
            foreach (var vm in _layers)
            {
                vm.PropertyChanged -= OnRowPropertyChanged;
                vm.Dispose();
            }
            _layers.Clear();
            _traced.Clear();

            if (layerStack is not null)
            {
                for (var i = layerStack.Count - 1; i >= 0; i--)
                {
                    var vm = new LayerViewModel(layerStack[i], OnLayerSelectionRequested, OnLayerDeleteRequested);
                    vm.PropertyChanged += OnRowPropertyChanged;
                    _traced[vm] = (vm.Name, vm.IsLocked);
                    _layers.Add(vm);
                }

                target = active is not null
                    ? _layers.FirstOrDefault(vm => vm.Layer == active)
                    : _layers.FirstOrDefault();
            }
        }
        finally
        {
            _isSyncing = false;
        }

        SelectedLayer = target;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LayerViewModel row || !_traced.TryGetValue(row, out var seen))
            return;

        if (e.PropertyName == nameof(LayerViewModel.IsLocked) && row.IsLocked != seen.Locked)
        {
            _traced[row] = (seen.Name, row.IsLocked);
            _trace?.Invoke($"Layer '{row.Name}' {(row.IsLocked ? "locked" : "unlocked")}.");
            NotifyCommandsCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(LayerViewModel.Name) && row.Name != seen.Name)
        {
            _traced[row] = (row.Name, seen.Locked);
            _trace?.Invoke($"Renamed a layer to '{row.Name}'.");
        }
    }
```

   Keep the doc comment that was on the original `SyncLayers`.

5. In `ImageEditorViewModel.cs` line 432, change the construction to:

```csharp
        LayerPanel = new LayerPanelViewModel(
            () => HasImage,
            message => _unifiedLogger?.Info(Domain.Services.UnifiedLogging.LogCategory.General, "ImageEditor", message));
```

   Check that `_unifiedLogger` is assigned before line 432 (it is assigned at line 428).

- [ ] **Step 5: Run the editor tests and confirm they pass**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LayerPanelViewModelTests|FullyQualifiedName~LayerViewModel|FullyQualifiedName~ImageEditor" -v q`
Expected: PASS. If an existing test asserts the old `SyncLayers` selected `_layers[0]` when `ActiveLayer` is null, that behaviour is unchanged (`FirstOrDefault()`).

- [ ] **Step 6: Swap the XAML**

In `DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml`, replace everything from `<!-- Layer Actions -->` through the `</Border>` that closes `<!-- Layer List -->` (lines 274–320) with:

```xml
                <!-- Layer list: the shared control (#594). Add/Duplicate are editor-only and sit in its
                     leading slot; ↑/↓/− are the control's own. Lock here also blocks Move/Transform. -->
                <reusable:LayerStackPanel Items="{Binding ImageEditor.LayerPanel.Layers}"
                                          SelectedItem="{Binding ImageEditor.LayerPanel.SelectedLayer, Mode=TwoWay}"
                                          MoveUpCommand="{Binding ImageEditor.LayerPanel.MoveLayerUpCommand}"
                                          MoveDownCommand="{Binding ImageEditor.LayerPanel.MoveLayerDownCommand}"
                                          DeleteCommand="{Binding ImageEditor.LayerPanel.DeleteLayerCommand}"
                                          LockToolTip="Lock the layer: it cannot be moved, transformed or deleted"
                                          ListMaxHeight="200">
                  <reusable:LayerStackPanel.LeadingTools>
                    <StackPanel Orientation="Horizontal" Spacing="2">
                      <Button Content="+" Command="{Binding ImageEditor.LayerPanel.AddLayerCommand}" Padding="6,2"
                              FontSize="12" ToolTip.Tip="Add new layer" FontWeight="Bold"/>
                      <Button Content="D" Command="{Binding ImageEditor.LayerPanel.DuplicateLayerCommand}"
                              Padding="6,2" FontSize="10" ToolTip.Tip="Duplicate selected layer"/>
                    </StackPanel>
                  </reusable:LayerStackPanel.LeadingTools>
                </reusable:LayerStackPanel>
```

The `reusable:` xmlns (`using:DiffusionNexus.UI.Views.Controls`) is already declared in this file. Leave the opacity inspector and the Merge/Flatten block below unchanged. Keep the file's LF line endings.

- [ ] **Step 7: Build and run the full suite**

Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -v q`, then `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -v q`
Expected: build success; all tests pass. The previous baseline was 5423 green; the new total is higher.

- [ ] **Step 8: Commit and push**

```bash
git add DiffusionNexus.UI/ViewModels/LayerViewModel.cs DiffusionNexus.UI/ViewModels/LayerPanelViewModel.cs DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml DiffusionNexus.Tests/ViewModels/LayerPanelViewModelTests.cs
git diff --cached --numstat; git diff --cached -w --numstat
git commit -m "feat(editor): Image Editor layer list on the shared LayerStackPanel; rename + lock (#594)

Lock now also disables delete. SyncLayers ignores the ListBox's null
write-back so a rebuild keeps the editor's active layer.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 9: GUI smoke, issue update, PR

**Files:** none changed, unless the smoke finds a defect. A fix goes on this branch as its own commit, with a test where one can observe it.

- [ ] **Step 1: Launch the app from this branch**

Run `dotnet run --project DiffusionNexus.UI` in the background (or the repo's usual launch). Open the Diffusion Canvas.

- [ ] **Step 2: Canvas smoke** (each line is pass/fail; record a screenshot for any visual item)

1. With an empty canvas, the layer column shows "Accepted results appear here as layers." and Clear canvas is disabled.
2. Generate a batch of 3 and Accept all. The column lists `Layer 3, Layer 2, Layer 1` (top first), `Layer 3` is selected, and the canvas shows the blue outline on it.
3. Select `Layer 1` and press ↑ twice. It moves to the top of the list and draws over the others on the canvas. ↑ is now disabled.
4. Put the box over a layer. The readout says image to image. Hide that layer (👁): it disappears and the readout switches to text to image. Generate, and the Unified Console's composite line says "Region is empty".
5. Set a layer to 50 % opacity. The canvas shows it translucent. Generate over it, and the console's composite line reports the region as covered.
6. Double-click a name, type `Sky`, press Enter. The row shows `Sky` and the console says `Renamed a layer to 'Sky'.` Repeat with Escape: the name is unchanged. Repeat with only spaces: the old name stays.
7. While renaming, type `F G B 1` and arrow keys. None of the canvas shortcuts fire (no fit or zoom, the grid does not toggle).
8. Lock a layer. − is disabled, right-click → "Delete result (locked: …)" is disabled, and Clear canvas removes only the unlocked layers; the console reports how many locked layers it kept. With only locked layers left, Clear canvas is disabled.
9. The Layers button hides and shows the column.
10. With candidates staged and a layer row focused, Delete discards the candidate and does not delete the layer.

- [ ] **Step 3: Image Editor smoke**

1. Open an image and switch to layer mode. The panel shows + D ↑ ↓ − and the rows.
2. Click rows: the active layer changes (paint goes to the selected layer).
3. Add, Duplicate, ↑, ↓ and − all work as before.
4. Rename a layer by double-click. Lock a layer: − is disabled, and the Move/Transform tool refuses it.
5. Toggle visibility, and use the opacity slider in the editor's inspector, which is unchanged.

- [ ] **Step 4: Tick the issue and open the PR**

Tick #594's checkboxes that the smoke proved, with a short comment for anything partial. Then:

```bash
gh pr create --base develop --head feature/canvas-layer-stack --milestone "Diffusion Canvas generation suite" \
  --title "Diffusion Canvas: layer stack with raster layers (#594)" --body-file <scratchpad>/pr-594.md
```

The PR body covers: what the user gets (canvas column, inspector, lock semantics, what the model sees); the Image Editor change (shared control, rename, lock disables delete, `SyncLayers` guard); out of scope; the smoke results; test counts; `Closes #594`. It ends with:

```
🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

Do **not** merge. The owner merges.
