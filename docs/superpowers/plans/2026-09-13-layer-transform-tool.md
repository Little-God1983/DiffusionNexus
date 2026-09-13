# Layer Move / Transform Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move, scale, rotate and flip the active layer of the Image Editor with Shape-style handles, while layers keep content that lies outside the canvas (GIMP model).

**Architecture:** `Layer` gains `OffsetX/OffsetY` and may be any size; every draw-a-layer and draw-into-a-layer site learns the offset. A new `LayerTransformTool` holds translation/scale/rotation/flips and exposes one canvas-space `SKMatrix`; the compositor previews it through a `LayerRenderOverride`; `ImageEditorCore.ApplyLayerTransform` rasterizes once on Enter into a new bitmap sized to the transformed bounds. Panel = `LayerTransformViewModel` + inline XAML in the Extend/Crop style; wiring mirrors `WireCanvasExtendEvents`.

**Tech Stack:** .NET 10, Avalonia 11.3, SkiaSharp 3.119.4, CommunityToolkit.Mvvm, xUnit 2.9 + FluentAssertions 8 (tests in `DiffusionNexus.Tests`, headless, no Avalonia init).

**Spec:** `docs/superpowers/specs/2026-09-13-layer-transform-tool-design.md`

## Global Constraints

- Repo: `E:\Repos\DiffusionNexus`, branch `feature/layer-transform-tool` (already created off `develop`, spec committed as e94af2e5). Commit after every task; push after every task (`git push`). Never commit to `develop`.
- Test command: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~<ClassName>" --nologo -v q` from the repo root. Full suite before the final push: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --nologo -v q`. Build the solution (`dotnet build DiffusionNexus.sln --nologo -v q`) before pushing so XAML errors surface.
- Every bitmap swap that the render thread can see happens under `ImageEditorCore._bitmapLock`; dispose the replaced bitmap **after** the lock (see the `_bitmapLock` doc comment in `ImageEditorCore.cs`).
- The inpaint mask layer (`Layer.IsInpaintMask`) stays canvas-sized at offset (0, 0) in every operation.
- SkiaSharp 3: `SKFilterQuality` is obsolete; use `new SKSamplingOptions(SKCubicResampler.Mitchell)`.
- Copy: Move toggle text `Move`; panel title `Move / Transform`; hint `Drag the layer to move it. Handles scale, the top handle rotates.`; ineligible hint `This layer can't be moved. Select another layer.`; too-large hint `The transformed layer would be too large.`; allocation hint `Could not allocate the transformed layer.`; status on open `Move: Drag the layer to move it, use the handles to scale or rotate. Enter applies, Escape resets.`
- Size guard: transformed width or height > 16384, or width × height > 268 435 456 → refuse.
- Handle geometry: `HandleRadius = 6f`, `HandleHitRadius = 12f`, `RotateHandleOffset = 30f`, rotation snap 15°, nudge 1 px / Shift 10 px.
- Tool edits emit LF: before each push run `git diff --numstat origin/develop -- <files>` vs `git diff -w --numstat`; if a pre-existing CRLF file shows whole-file churn, restore CRLF on that file only (`unix2dos` or `sed -i 's/$/\r/'`) before committing.
- No new entry in `DiffusionNexus.UI/REUSABLES.md` (no reusable control is created).
- Every feature step logs to the Unified Console via `IUnifiedLogger` (source `LayerTransform`), as `CanvasExtendViewModel.EmitInfo` does.

---

### Task 1: Layer gains offset and bounds

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/Layer.cs`
- Test: `DiffusionNexus.Tests/ImageEditor/LayerBoundsTests.cs` (create)

**Interfaces:**
- Produces: `int Layer.OffsetX`, `int Layer.OffsetY`, `SKRectI Layer.Bounds`, `Layer(SKBitmap source, string name, SKPointI offset)`, `internal void Layer.AdoptBitmap(SKBitmap bitmap, SKPointI offset)`, `internal void Layer.SetOffset(int x, int y)`. `PropertyChanged` fires with `"Offset"`.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// A layer has a position (OffsetX/OffsetY in canvas pixels) and its own size; Bounds combines
/// them. Offsets default to zero so untouched documents behave exactly as before.
/// </summary>
public class LayerBoundsTests
{
    private static SKBitmap Red(int w, int h)
    {
        var b = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        b.Erase(SKColors.Red);
        return b;
    }

    [Fact]
    public void NewLayer_HasZeroOffset_AndCanvasBounds()
    {
        using var layer = new Layer(40, 30, "L");
        layer.OffsetX.Should().Be(0);
        layer.OffsetY.Should().Be(0);
        layer.Bounds.Should().Be(new SKRectI(0, 0, 40, 30));
    }

    [Fact]
    public void OffsetConstructor_PlacesTheLayer()
    {
        using var src = Red(10, 8);
        using var layer = new Layer(src, "L", new SKPointI(-3, 5));
        layer.Bounds.Should().Be(new SKRectI(-3, 5, 7, 13));
    }

    [Fact]
    public void AdoptBitmapWithOffset_SwapsBoth_AndRaisesContentChanged()
    {
        using var layer = new Layer(10, 10, "L");
        var raised = 0;
        layer.ContentChanged += (_, _) => raised++;
        var replacement = Red(4, 6);

        layer.AdoptBitmap(replacement, new SKPointI(20, -2));

        layer.Bitmap.Should().BeSameAs(replacement);
        layer.Bounds.Should().Be(new SKRectI(20, -2, 24, 4));
        raised.Should().Be(1);
    }

    [Fact]
    public void SetOffset_RaisesPropertyChangedOffset_AndContentChanged()
    {
        using var layer = new Layer(10, 10, "L");
        string? prop = null;
        var content = 0;
        layer.PropertyChanged += (_, p) => prop = p;
        layer.ContentChanged += (_, _) => content++;

        layer.SetOffset(7, 9);

        prop.Should().Be("Offset");
        content.Should().Be(1);
        layer.OffsetX.Should().Be(7);
        layer.OffsetY.Should().Be(9);
    }

    [Fact]
    public void SetOffset_SameValue_RaisesNothing()
    {
        using var layer = new Layer(10, 10, "L");
        var content = 0;
        layer.ContentChanged += (_, _) => content++;
        layer.SetOffset(0, 0);
        content.Should().Be(0);
    }

    [Fact]
    public void Clone_CopiesOffset()
    {
        using var src = Red(5, 5);
        using var layer = new Layer(src, "L", new SKPointI(11, 12));
        using var clone = layer.Clone();
        clone.Bounds.Should().Be(layer.Bounds);
    }

    [Fact]
    public void ReplaceBitmap_KeepsOffset()
    {
        using var src = Red(5, 5);
        using var layer = new Layer(src, "L", new SKPointI(11, 12));
        layer.ReplaceBitmap(Red(5, 5));
        layer.OffsetX.Should().Be(11);
        layer.OffsetY.Should().Be(12);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LayerBoundsTests" --nologo -v q`
Expected: build error — `OffsetX`, `Bounds`, offset constructor do not exist.

- [ ] **Step 3: Implement in `Layer.cs`**

Add fields after `_isDisposed`:

```csharp
    private int _offsetX;
    private int _offsetY;
```

Add the constructor overload after `Layer(SKBitmap sourceBitmap, string name = "Layer")`:

```csharp
    /// <summary>
    /// Creates a layer from an existing bitmap placed at <paramref name="offset"/> (canvas pixels).
    /// </summary>
    public Layer(SKBitmap sourceBitmap, string name, SKPointI offset) : this(sourceBitmap, name)
    {
        _offsetX = offset.X;
        _offsetY = offset.Y;
    }
```

Add properties after `Height`:

```csharp
    /// <summary>Left edge of this layer in canvas pixels. Zero for a canvas-aligned layer.</summary>
    public int OffsetX => _offsetX;

    /// <summary>Top edge of this layer in canvas pixels. Zero for a canvas-aligned layer.</summary>
    public int OffsetY => _offsetY;

    /// <summary>The layer's rectangle in canvas pixels: offset plus its own bitmap size.</summary>
    public SKRectI Bounds => new(_offsetX, _offsetY, _offsetX + Width, _offsetY + Height);

    /// <summary>
    /// Moves the layer without touching its pixels. Raises <see cref="PropertyChanged"/> with
    /// "Offset" and <see cref="ContentChanged"/> so the compositor redraws.
    /// </summary>
    internal void SetOffset(int x, int y)
    {
        if (_offsetX == x && _offsetY == y) return;
        _offsetX = x;
        _offsetY = y;
        PropertyChanged?.Invoke(this, "Offset");
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
```

Add next to the existing `AdoptBitmap(SKBitmap)`:

```csharp
    /// <summary>Replaces the bitmap and the offset together (one ContentChanged).</summary>
    internal void AdoptBitmap(SKBitmap newBitmap, SKPointI offset)
    {
        _offsetX = offset.X;
        _offsetY = offset.Y;
        AdoptBitmap(newBitmap);
    }
```

In `Clone()` change `var clone = new Layer(_bitmap, $"{_name} Copy")` to `var clone = new Layer(_bitmap, $"{_name} Copy", new SKPointI(_offsetX, _offsetY))`.

- [ ] **Step 4: Run tests**

Run the same filter. Expected: 7 passed.

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/ImageEditor/Layer.cs DiffusionNexus.Tests/ImageEditor/LayerBoundsTests.cs
git commit -m "feat(editor): layers carry an offset and their own bounds

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 2: Compositor draws at the offset, clips to the canvas, accepts a preview override

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/LayerCompositor.cs`
- Test: `DiffusionNexus.Tests/ImageEditor/LayerCompositorOffsetTests.cs` (create)

**Interfaces:**
- Consumes: `Layer.OffsetX/OffsetY/Bounds` (Task 1).
- Produces: `public readonly record struct LayerRenderOverride(Layer Layer, SKMatrix Matrix);` and `LayerCompositor.CompositeToCanvas(SKCanvas canvas, LayerStack layers, SKRect destRect, LayerRenderOverride? previewOverride = null)`. `ExportLayersAsBitmaps` now returns `List<(string Name, SKBitmap Bitmap, float Opacity, BlendMode BlendMode, SKPointI Offset)>`.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Compositing honours each layer's offset, clips to the canvas, and can preview one layer
/// through a matrix override without touching the layer.
/// </summary>
public class LayerCompositorOffsetTests : IDisposable
{
    private readonly LayerStack _stack = new(20, 20);

    public void Dispose() => _stack.Dispose();

    private static SKBitmap Solid(int w, int h, SKColor c)
    {
        var b = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        b.Erase(c);
        return b;
    }

    private SKBitmap Composite(LayerRenderOverride? over = null)
    {
        var result = new SKBitmap(20, 20, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        LayerCompositor.CompositeToCanvas(canvas, _stack, new SKRect(0, 0, 20, 20), over);
        return result;
    }

    [Fact]
    public void LayerAtOffset_LandsAtOffset()
    {
        using var src = Solid(4, 4, SKColors.Red);
        _stack.AddLayerFromBitmap(src, "L", new SKPointI(10, 5));

        using var result = Composite();

        result.GetPixel(10, 5).Should().Be(SKColors.Red);
        result.GetPixel(13, 8).Should().Be(SKColors.Red);
        result.GetPixel(9, 5).Alpha.Should().Be(0);
        result.GetPixel(14, 8).Alpha.Should().Be(0);
    }

    [Fact]
    public void ContentOutsideCanvas_IsClipped_AndDoesNotWrap()
    {
        using var src = Solid(30, 30, SKColors.Blue);
        _stack.AddLayerFromBitmap(src, "L", new SKPointI(-15, -15));

        using var result = Composite();

        result.GetPixel(0, 0).Should().Be(SKColors.Blue);
        result.GetPixel(14, 14).Should().Be(SKColors.Blue);
        result.GetPixel(15, 15).Alpha.Should().Be(0);
    }

    [Fact]
    public void Override_MovesOnlyThatLayer_AndLeavesTheLayerUntouched()
    {
        using var red = Solid(4, 4, SKColors.Red);
        using var green = Solid(4, 4, SKColors.Green);
        var redLayer = _stack.AddLayerFromBitmap(red, "R", new SKPointI(0, 0));
        _stack.AddLayerFromBitmap(green, "G", new SKPointI(10, 10));

        using var result = Composite(new LayerRenderOverride(redLayer, SKMatrix.CreateTranslation(5, 0)));

        result.GetPixel(0, 0).Alpha.Should().Be(0);
        result.GetPixel(5, 0).Should().Be(SKColors.Red);
        result.GetPixel(10, 10).Should().Be(SKColors.Green);
        redLayer.Bounds.Should().Be(new SKRectI(0, 0, 4, 4));
    }

    [Fact]
    public void FlattenedBitmap_HonoursOffset()
    {
        using var src = Solid(2, 2, SKColors.Red);
        _stack.AddLayerFromBitmap(src, "L", new SKPointI(3, 4));

        using var flat = LayerCompositor.CreateFlattenedBitmap(_stack);

        flat.GetPixel(3, 4).Should().Be(SKColors.Red);
        flat.GetPixel(0, 0).Alpha.Should().Be(0);
    }

    [Fact]
    public void ExportLayersAsBitmaps_ReportsOffsets()
    {
        using var src = Solid(2, 2, SKColors.Red);
        _stack.AddLayerFromBitmap(src, "L", new SKPointI(3, 4));

        var exported = LayerCompositor.ExportLayersAsBitmaps(_stack);

        exported.Should().ContainSingle().Which.Offset.Should().Be(new SKPointI(3, 4));
        foreach (var e in exported) e.Bitmap.Dispose();
    }
}
```

`AddLayerFromBitmap(bitmap, name, offset)` on `LayerStack` is added in this task too (see Step 3), because the tests need it.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~LayerCompositorOffsetTests" --nologo -v q`
Expected: build errors (`LayerRenderOverride`, 3-arg `AddLayerFromBitmap`, `Offset` tuple member).

- [ ] **Step 3: Implement**

In `LayerStack.cs`, change `AddLayerFromBitmap` signature and body:

```csharp
    public Layer AddLayerFromBitmap(SKBitmap bitmap, string? name = null, SKPointI offset = default)
    {
        var layerName = name ?? $"Layer {_layers.Count + 1}";
        var layer = new Layer(bitmap, layerName, offset);
        // ... rest unchanged
```

In `LayerCompositor.cs`, add above the class:

```csharp
/// <summary>
/// Draws one layer through <paramref name="Matrix"/> (canvas coordinates) instead of at its
/// stored offset. The transform tool's live preview: nothing about the layer changes.
/// </summary>
public readonly record struct LayerRenderOverride(Layer Layer, SKMatrix Matrix);
```

Replace `CompositeToCanvas`:

```csharp
    public static void CompositeToCanvas(SKCanvas canvas, LayerStack layers, SKRect destRect, LayerRenderOverride? previewOverride = null)
    {
        if (layers.Count == 0) return;

        var scaleX = destRect.Width / layers.Width;
        var scaleY = destRect.Height / layers.Height;

        canvas.Save();
        canvas.Translate(destRect.Left, destRect.Top);
        canvas.Scale(scaleX, scaleY);
        // Layers may extend past the canvas; only the canvas is shown.
        canvas.ClipRect(new SKRect(0, 0, layers.Width, layers.Height));

        foreach (var layer in layers.Layers)
        {
            if (!layer.IsVisible || layer.Bitmap == null) continue;

            if (layer.IsInpaintMask)
            {
                RenderInpaintMaskLayer(canvas, layer, layers.Width, layers.Height);
                continue;
            }

            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)(layer.Opacity * 255)),
                BlendMode = layer.BlendMode.ToSKBlendMode(),
                IsAntialias = true
            };

            if (previewOverride is { } over && ReferenceEquals(over.Layer, layer))
            {
                canvas.Save();
                canvas.Concat(over.Matrix);
                canvas.DrawBitmap(layer.Bitmap, layer.OffsetX, layer.OffsetY, paint);
                canvas.Restore();
            }
            else
            {
                canvas.DrawBitmap(layer.Bitmap, layer.OffsetX, layer.OffsetY, paint);
            }
        }

        canvas.Restore();
    }
```

`RenderLayer`: change the draw to honour the offset inside `destRect` scaled space — replace the body's last line with:

```csharp
        var sx = destRect.Width / layer.Width;
        var sy = destRect.Height / layer.Height;
        canvas.DrawBitmap(layer.Bitmap, destRect, paint);
```

(Leave `RenderLayer` semantics as "draw this layer into this rect"; it has no callers that pass canvas rects. Only add the `sx/sy` lines if a compiler warning about unused variables would not fire — otherwise leave `RenderLayer` untouched.)

`ExportLayersAsBitmaps`:

```csharp
    public static List<(string Name, SKBitmap Bitmap, float Opacity, BlendMode BlendMode, SKPointI Offset)> ExportLayersAsBitmaps(LayerStack layers)
    {
        var result = new List<(string, SKBitmap, float, BlendMode, SKPointI)>();
        foreach (var layer in layers.Layers)
        {
            if (layer.Bitmap == null) continue;
            result.Add((layer.Name, layer.Bitmap.Copy(), layer.Opacity, layer.BlendMode, new SKPointI(layer.OffsetX, layer.OffsetY)));
        }
        return result;
    }
```

`CreateFlattenedBitmap` needs no change (it calls `CompositeToCanvas`).

- [ ] **Step 4: Run tests** — expected 5 passed. Also run `--filter "FullyQualifiedName~ImageEditor"` to confirm no regression (the existing compositor behaviour is the offset-0 special case).

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/ImageEditor/LayerCompositor.cs DiffusionNexus.UI/ImageEditor/LayerStack.cs DiffusionNexus.Tests/ImageEditor/LayerCompositorOffsetTests.cs
git commit -m "feat(editor): compositor honours layer offsets and previews a matrix override

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 3: LayerStack operations become offset-aware (Flatten, MergeDown, CropAll, ResizeCanvas, TransformAll)

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/LayerStack.cs`, `DiffusionNexus.UI/ImageEditor/Layer.cs`, `DiffusionNexus.UI/ImageEditor/ImageEditorCore.Transforms.cs`, `DiffusionNexus.UI/ImageEditor/Services/ILayerManager.cs`, `DiffusionNexus.UI/ImageEditor/Services/LayerManager.cs`
- Test: `DiffusionNexus.Tests/ImageEditor/LayerStackOffsetTests.cs` (create)

**Interfaces:**
- Consumes: Task 1 members.
- Produces: `LayerStack.TransformAll(Func<Layer, (SKBitmap Bitmap, SKPointI Offset)?> transform, int newWidth, int newHeight)`; `ILayerManager.AddLayerFromBitmap(SKBitmap bitmap, string? name = null, SKPointI offset = default)`; `ILayerManager.EnableLayerMode(int canvasWidth, int canvasHeight)`; `internal SKBitmap Layer.CreateResizedBitmap(int newWidth, int newHeight, int offsetX, int offsetY, out SKPointI newOffset)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Stack-wide operations on layers that have their own bounds: Flatten draws at the offset,
/// Merge Down unions both layers, Crop clips to the new canvas, Extend grows to the union with
/// the canvas, whole-image rotate/flip remap the offset.
/// </summary>
public class LayerStackOffsetTests : IDisposable
{
    private readonly LayerStack _stack = new(20, 10);

    public void Dispose() => _stack.Dispose();

    private static SKBitmap Solid(int w, int h, SKColor c)
    {
        var b = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        b.Erase(c);
        return b;
    }

    private Layer Add(int w, int h, SKColor c, int x, int y, string name = "L")
    {
        using var src = Solid(w, h, c);
        return _stack.AddLayerFromBitmap(src, name, new SKPointI(x, y));
    }

    [Fact]
    public void Flatten_DrawsAtOffset_AndClips()
    {
        Add(4, 4, SKColors.Red, 18, 8);
        using var flat = _stack.Flatten()!;
        flat.Width.Should().Be(20);
        flat.GetPixel(18, 8).Should().Be(SKColors.Red);
        flat.GetPixel(17, 8).Alpha.Should().Be(0);
    }

    [Fact]
    public void MergeDown_ProducesUnionBounds_AndKeepsOffCanvasPixels()
    {
        var below = Add(4, 4, SKColors.Red, 0, 0, "below");
        var top = Add(4, 4, SKColors.Blue, 22, 2, "top"); // wholly off canvas

        _stack.MergeDown(top).Should().BeTrue();

        _stack.Count.Should().Be(1);
        below.Bounds.Should().Be(new SKRectI(0, 0, 26, 6));
        below.Bitmap!.GetPixel(0, 0).Should().Be(SKColors.Red);
        below.Bitmap!.GetPixel(23, 3).Should().Be(SKColors.Blue);
        below.Bitmap!.GetPixel(10, 5).Alpha.Should().Be(0);
    }

    [Fact]
    public void CropAll_ClipsEachLayer_AndReoffsets()
    {
        var layer = Add(10, 10, SKColors.Red, -5, -5); // covers canvas 0..5
        _stack.CropAll(new SKRectI(2, 2, 8, 8));

        _stack.Width.Should().Be(6);
        _stack.Height.Should().Be(6);
        layer.Bounds.Should().Be(new SKRectI(0, 0, 3, 3)); // intersection (2,2)-(5,5) relative to (2,2)
        layer.Bitmap!.GetPixel(0, 0).Should().Be(SKColors.Red);
    }

    [Fact]
    public void CropAll_LayerWhollyOutside_BecomesOnePixelTransparent()
    {
        var layer = Add(2, 2, SKColors.Red, 30, 30);
        _stack.CropAll(new SKRectI(0, 0, 10, 10));
        layer.Bounds.Should().Be(new SKRectI(0, 0, 1, 1));
        layer.Bitmap!.GetPixel(0, 0).Alpha.Should().Be(0);
    }

    [Fact]
    public void ResizeCanvas_GrowsToUnionWithCanvas_AndShifts()
    {
        var full = Add(20, 10, SKColors.Red, 0, 0, "bg");
        var moved = Add(4, 4, SKColors.Blue, 25, 3, "moved"); // off canvas to the right

        _stack.ResizeCanvas(30, 20, 5, 5); // add 5 px left and top

        _stack.Width.Should().Be(30);
        full.Bounds.Should().Be(new SKRectI(0, 0, 30, 20));
        full.Bitmap!.GetPixel(5, 5).Should().Be(SKColors.Red);
        full.Bitmap!.GetPixel(0, 0).Alpha.Should().Be(0);
        // moved layer: shifted to (30,8)-(34,12), union with canvas (0,0)-(30,20) => (0,0)-(34,20)
        moved.Bounds.Should().Be(new SKRectI(0, 0, 34, 20));
        moved.Bitmap!.GetPixel(30, 8).Should().Be(SKColors.Blue);
    }

    [Theory]
    [InlineData("right", 10, 20, 4, 3)]   // (H-(y+h), x) = (10-(3+3), 3) = (4,3)
    [InlineData("left", 10, 20, 3, 15)]   // (y, W-(x+w)) = (3, 20-(3+2)) = (3,15)
    [InlineData("180", 20, 10, 15, 4)]    // (W-(x+w), H-(y+h)) = (15, 4)
    [InlineData("flipH", 20, 10, 15, 3)]  // (W-(x+w), y)
    [InlineData("flipV", 20, 10, 3, 4)]   // (x, H-(y+h))
    public void TransformAll_RemapsOffsets(string op, int expectedW, int expectedH, int ox, int oy)
    {
        using var core = new ImageEditorCoreProbe(_stack);
        var layer = Add(2, 3, SKColors.Red, 3, 3);

        core.Apply(op);

        _stack.Width.Should().Be(expectedW);
        _stack.Height.Should().Be(expectedH);
        layer.OffsetX.Should().Be(ox);
        layer.OffsetY.Should().Be(oy);
    }

    /// <summary>Drives the stack through the same remap the core uses (LayerOffsetRemap).</summary>
    private sealed class ImageEditorCoreProbe(LayerStack stack) : IDisposable
    {
        public void Apply(string op)
        {
            var w = stack.Width; var h = stack.Height;
            switch (op)
            {
                case "right": stack.TransformAll(l => (Rot(l.Bitmap!, 90), LayerOffsetRemap.RotateRight(l.Bounds, w, h)), h, w); break;
                case "left": stack.TransformAll(l => (Rot(l.Bitmap!, -90), LayerOffsetRemap.RotateLeft(l.Bounds, w, h)), h, w); break;
                case "180": stack.TransformAll(l => (Rot(l.Bitmap!, 180), LayerOffsetRemap.Rotate180(l.Bounds, w, h)), w, h); break;
                case "flipH": stack.TransformAll(l => (l.Bitmap!.Copy(), LayerOffsetRemap.FlipHorizontal(l.Bounds, w, h)), w, h); break;
                case "flipV": stack.TransformAll(l => (l.Bitmap!.Copy(), LayerOffsetRemap.FlipVertical(l.Bounds, w, h)), w, h); break;
            }
        }

        private static SKBitmap Rot(SKBitmap src, int deg)
        {
            var swap = deg != 180;
            var r = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height);
            using var c = new SKCanvas(r);
            c.Translate(r.Width / 2f, r.Height / 2f);
            c.RotateDegrees(deg);
            c.Translate(-src.Width / 2f, -src.Height / 2f);
            c.DrawBitmap(src, 0, 0);
            return r;
        }

        public void Dispose() { }
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `LayerStackOffsetTests`. Expected: build errors (`LayerOffsetRemap`, new `TransformAll` overload).

- [ ] **Step 3: Implement**

**3a. `Layer.cs`** — replace `Crop`, `CreateResizedBitmap`, `ResizeCanvas`:

```csharp
    /// <summary>
    /// Crops the layer to <paramref name="cropRect"/> (canvas pixels): keeps the intersection of
    /// the layer's bounds with the rect and re-offsets relative to the rect's top-left. A layer
    /// wholly outside becomes a 1x1 transparent bitmap at (0, 0) so it stays valid.
    /// </summary>
    public void Crop(SKRectI cropRect)
    {
        if (_bitmap == null || cropRect.Width <= 0 || cropRect.Height <= 0) return;

        var inter = SKRectI.Intersect(Bounds, cropRect);
        if (inter.IsEmpty || inter.Width <= 0 || inter.Height <= 0)
        {
            var empty = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
            empty.Erase(SKColors.Transparent);
            AdoptBitmap(empty, new SKPointI(0, 0));
            return;
        }

        var newBitmap = new SKBitmap(inter.Width, inter.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        newBitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(newBitmap))
        {
            // Source rect in layer-local pixels.
            var src = new SKRect(inter.Left - _offsetX, inter.Top - _offsetY, inter.Right - _offsetX, inter.Bottom - _offsetY);
            canvas.DrawBitmap(_bitmap, src, new SKRect(0, 0, inter.Width, inter.Height));
        }
        AdoptBitmap(newBitmap, new SKPointI(inter.Left - cropRect.Left, inter.Top - cropRect.Top));
    }

    /// <summary>
    /// Builds the bitmap <see cref="ResizeCanvas"/> would swap in, without touching the layer.
    /// The layer is shifted by (<paramref name="offsetX"/>, <paramref name="offsetY"/>) and grown
    /// to the union of its shifted bounds and the new canvas, so a canvas-aligned layer stays
    /// canvas-aligned (drawing on the new area keeps working) and a moved layer keeps every pixel.
    /// Throws when SkiaSharp cannot allocate, so the caller can report the failure.
    /// </summary>
    internal SKBitmap CreateResizedBitmap(int newWidth, int newHeight, int offsetX, int offsetY, out SKPointI newOffset)
    {
        if (_bitmap == null)
            throw new InvalidOperationException("The layer has no bitmap to resize.");

        var shifted = new SKRectI(_offsetX + offsetX, _offsetY + offsetY, _offsetX + offsetX + Width, _offsetY + offsetY + Height);
        var union = SKRectI.Union(shifted, new SKRectI(0, 0, newWidth, newHeight));

        var newBitmap = new SKBitmap(union.Width, union.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (newBitmap.IsEmpty || newBitmap.Width != union.Width || newBitmap.Height != union.Height)
        {
            newBitmap.Dispose();
            throw new InvalidOperationException($"Could not allocate a {union.Width}x{union.Height} canvas.");
        }
        newBitmap.Erase(SKColors.Transparent);

        using var canvas = new SKCanvas(newBitmap);
        canvas.DrawBitmap(_bitmap, shifted.Left - union.Left, shifted.Top - union.Top);
        newOffset = new SKPointI(union.Left, union.Top);
        return newBitmap;
    }

    /// <summary>Resizes the layer canvas and draws the existing content at the specified offset.</summary>
    public void ResizeCanvas(int newWidth, int newHeight, int offsetX, int offsetY)
    {
        if (_bitmap == null) return;
        AdoptBitmap(CreateResizedBitmap(newWidth, newHeight, offsetX, offsetY, out var newOffset), newOffset);
    }
```

Delete the old 4-parameter `CreateResizedBitmap` (its only caller is `LayerStack.ResizeCanvas`, updated below).

**3b. New file `DiffusionNexus.UI/ImageEditor/LayerOffsetRemap.cs`:**

```csharp
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Where a layer's top-left lands after a whole-image rotate/flip. <c>canvasWidth/Height</c> are
/// the canvas size BEFORE the operation; <c>bounds</c> the layer's bounds before it.
/// </summary>
public static class LayerOffsetRemap
{
    public static SKPointI RotateRight(SKRectI b, int canvasWidth, int canvasHeight) => new(canvasHeight - b.Bottom, b.Left);
    public static SKPointI RotateLeft(SKRectI b, int canvasWidth, int canvasHeight) => new(b.Top, canvasWidth - b.Right);
    public static SKPointI Rotate180(SKRectI b, int canvasWidth, int canvasHeight) => new(canvasWidth - b.Right, canvasHeight - b.Bottom);
    public static SKPointI FlipHorizontal(SKRectI b, int canvasWidth, int canvasHeight) => new(canvasWidth - b.Right, b.Top);
    public static SKPointI FlipVertical(SKRectI b, int canvasWidth, int canvasHeight) => new(b.Left, canvasHeight - b.Bottom);
}
```

**3c. `LayerStack.cs`:**

`Flatten` — replace the draw line with `canvas.DrawBitmap(layer.Bitmap, layer.OffsetX, layer.OffsetY, paint);` and add `canvas.ClipRect(new SKRect(0, 0, _width, _height));` after `canvas.Clear`.

`MergeDown` — replace the body from `// Draw the top layer onto the bottom layer` to `belowLayer.NotifyContentChanged();` with:

```csharp
        var union = SKRectI.Union(belowLayer.Bounds, layer.Bounds);
        var merged = new SKBitmap(union.Width, union.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        merged.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(merged))
        {
            canvas.DrawBitmap(belowLayer.Bitmap, belowLayer.OffsetX - union.Left, belowLayer.OffsetY - union.Top);
            if (layer.IsVisible)
            {
                using var paint = new SKPaint
                {
                    Color = SKColors.White.WithAlpha((byte)(layer.Opacity * 255)),
                    BlendMode = layer.BlendMode.ToSKBlendMode()
                };
                canvas.DrawBitmap(layer.Bitmap, layer.OffsetX - union.Left, layer.OffsetY - union.Top, paint);
            }
        }
        belowLayer.AdoptBitmap(merged, new SKPointI(union.Left, union.Top));
```

(`AdoptBitmap` raises `ContentChanged`; drop the separate `NotifyContentChanged` call. Keep the `belowLayer.Bitmap == null || layer.Bitmap == null` guard; remove the `CreateCanvas` usage.)

`CropAll` — unchanged apart from relying on the new `Layer.Crop`; keep the mask layer canvas-sized: after the loop nothing extra is needed because a canvas-sized mask at (0,0) crops to exactly the new canvas at (0,0).

`ResizeCanvas` — the tuple becomes `(Layer Layer, SKBitmap Bitmap, SKPointI Offset)`; call `layer.CreateResizedBitmap(newWidth, newHeight, offsetX, offsetY, out var off)` and `layer.AdoptBitmap(bitmap, offset)`.

`TransformAll` — replace with:

```csharp
    /// <summary>
    /// Applies a whole-image transform to every layer. <paramref name="transform"/> returns the
    /// transformed bitmap and the layer's new offset (see <see cref="LayerOffsetRemap"/>); the
    /// stack takes the new canvas size from the caller, never from a layer.
    /// </summary>
    public void TransformAll(Func<Layer, (SKBitmap Bitmap, SKPointI Offset)?> transform, int newWidth, int newHeight)
    {
        foreach (var layer in _layers)
        {
            var result = transform(layer);
            if (result is { } r)
                layer.AdoptBitmap(r.Bitmap, r.Offset);
        }

        _width = newWidth;
        _height = newHeight;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
```

**3d. `ImageEditorCore.Transforms.cs`** — the five public methods become:

```csharp
    public bool RotateRight() => ApplyTransform(RotateBitmapRight, LayerOffsetRemap.RotateRight, swapsAxes: true);
    public bool RotateLeft() => ApplyTransform(RotateBitmapLeft, LayerOffsetRemap.RotateLeft, swapsAxes: true);
    public bool Rotate180() => ApplyTransform(RotateBitmap180, LayerOffsetRemap.Rotate180, swapsAxes: false);
    public bool FlipHorizontal() => ApplyTransform(FlipBitmapHorizontal, LayerOffsetRemap.FlipHorizontal, swapsAxes: false);
    public bool FlipVertical() => ApplyTransform(FlipBitmapVertical, LayerOffsetRemap.FlipVertical, swapsAxes: false);
```

and `ApplyTransform` takes `(Func<SKBitmap?, SKBitmap?> transform, Func<SKRectI, int, int, SKPointI> remap, bool swapsAxes)`; inside the layer branch:

```csharp
                if (_isLayerMode && _layers != null)
                {
                    var w = _layers.Width; var h = _layers.Height;
                    _layers.TransformAll(layer =>
                    {
                        var bmp = transform(layer.Bitmap);
                        return bmp is null ? null : (bmp, remap(layer.Bounds, w, h));
                    }, swapsAxes ? h : w, swapsAxes ? w : h);
                }
```

**3e. `ILayerManager` / `LayerManager`:**

```csharp
    /// <summary>Adds a layer from a bitmap placed at <paramref name="offset"/> (canvas pixels).</summary>
    Layer? AddLayerFromBitmap(SKBitmap bitmap, string? name = null, SKPointI offset = default);

    /// <summary>Starts layer mode with an EMPTY stack of the given canvas size (TIFF import adds layers next).</summary>
    void EnableLayerMode(int canvasWidth, int canvasHeight);
```

`LayerManager` implementations:

```csharp
    public Layer? AddLayerFromBitmap(SKBitmap bitmap, string? name = null, SKPointI offset = default)
    {
        if (!_isLayerMode || _stack is null) return null;
        return _stack.AddLayerFromBitmap(bitmap, name, offset);
    }

    public void EnableLayerMode(int canvasWidth, int canvasHeight)
    {
        if (_stack != null) return;
        _stack = new LayerStack(canvasWidth, canvasHeight);
        _stack.ContentChanged += OnStackContentChanged;
        _stack.LayersChanged += OnStackLayersChanged;
        _isLayerMode = true;
        LayerModeChanged?.Invoke(this, EventArgs.Empty);
    }
```

- [ ] **Step 4: Run tests** — filters `LayerStackOffsetTests`, `LayerManagerTests`, `ImageEditorCoreCanvasExtendTests`, `ImageEditorCoreRenderRaceTests`. Expected: all green. If `ImageEditorCoreCanvasExtendTests` asserted the old all-or-nothing on a 4-arg `CreateResizedBitmap`, update the call site in that test to the `out` overload.

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/ImageEditor DiffusionNexus.Tests/ImageEditor/LayerStackOffsetTests.cs
git commit -m "feat(editor): stack operations honour layer bounds (union merge, clipping crop, offset remap)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 4: Drawing into an offset layer, background removal offsets, TIFF import path

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs` (`ApplyStroke`, `ApplyShape`, `LoadLayeredTiff`), `DiffusionNexus.UI/ImageEditor/ImageEditorCore.Inpainting.cs` (`ApplyInpaintStroke`), `DiffusionNexus.UI/ImageEditor/ImageEditorCore.BackgroundOps.cs`
- Test: `DiffusionNexus.Tests/ImageEditor/ImageEditorCoreOffsetLayerTests.cs` (create)

**Interfaces:**
- Consumes: Task 3 `AddLayerFromBitmap(bitmap, name, offset)`, `EnableLayerMode(w, h)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Pixel tools address the active layer in CANVAS coordinates; a layer with an offset receives
/// the paint at the right place and nothing lands outside its bounds.
/// </summary>
public class ImageEditorCoreOffsetLayerTests : IDisposable
{
    private readonly ImageEditorCore _sut = new();
    private readonly EditorServices _services = EditorServiceFactory.Create();

    public ImageEditorCoreOffsetLayerTests()
    {
        _sut.SetServices(_services);
        using var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray());
        _sut.EnableLayerMode();
    }

    public void Dispose() => _sut.Dispose();

    private Layer AddOffsetLayer()
    {
        using var small = new SKBitmap(20, 20, SKColorType.Rgba8888, SKAlphaType.Premul);
        small.Erase(SKColors.Transparent);
        var layer = _services.Layers.AddLayerFromBitmap(small, "small", new SKPointI(50, 50))!;
        _sut.ActiveLayer = layer;
        return layer;
    }

    [Fact]
    public void ApplyStroke_OnOffsetLayer_LandsInLayerLocalPixels()
    {
        var layer = AddOffsetLayer();
        // Canvas point (60,60) => layer-local (10,10). Brush 4 px of 100 => 0.04.
        _sut.ApplyStroke([new SKPoint(0.6f, 0.6f)], SKColors.Red, 0.04f, BrushShape.Square).Should().BeTrue();

        layer.Bitmap!.GetPixel(10, 10).Red.Should().BeGreaterThan(200);
        layer.Bitmap!.GetPixel(0, 0).Alpha.Should().Be(0);
    }

    [Fact]
    public void ApplyStroke_OutsideLayerBounds_DrawsNothing()
    {
        var layer = AddOffsetLayer();
        _sut.ApplyStroke([new SKPoint(0.1f, 0.1f)], SKColors.Red, 0.04f, BrushShape.Square).Should().BeTrue();
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++)
                layer.Bitmap!.GetPixel(x, y).Alpha.Should().Be(0);
    }

    [Fact]
    public void ApplyShape_OnOffsetLayer_UsesCanvasCoordinates()
    {
        var layer = AddOffsetLayer();
        var shape = new ShapeData
        {
            ShapeType = ShapeType.Rectangle, FillMode = ShapeFillMode.Fill,
            StrokeColor = SKColors.Blue, FillColor = SKColors.Blue, StrokeWidth = 0.01f,
            NormalizedStart = new SKPoint(0.55f, 0.55f), NormalizedEnd = new SKPoint(0.65f, 0.65f)
        };
        _sut.ApplyShape(shape).Should().BeTrue();
        layer.Bitmap!.GetPixel(10, 10).Blue.Should().BeGreaterThan(200);
        layer.Bitmap!.GetPixel(1, 1).Alpha.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `ImageEditorCoreOffsetLayerTests`. Expected: the two "lands" tests fail (paint currently lands at layer-local (60,60), outside the 20×20 bitmap → nothing drawn); the "outside" test may pass already.

- [ ] **Step 3: Implement**

In `ApplyStroke` (ImageEditorCore.cs) replace

```csharp
                var width = targetBitmap.Width;
                var height = targetBitmap.Height;
```
with
```csharp
                // Canvas size, not the layer's: strokes arrive in canvas-normalised coordinates.
                var width = targetLayer is not null && _layers is not null ? _layers.Width : targetBitmap.Width;
                var height = targetLayer is not null && _layers is not null ? _layers.Height : targetBitmap.Height;
```
and after `using var canvas = new SKCanvas(targetBitmap);` add
```csharp
                if (targetLayer is not null)
                    canvas.Translate(-targetLayer.OffsetX, -targetLayer.OffsetY);
```

Apply the identical two edits in `ApplyShape` (same variable names `targetBitmap`, `targetLayer`, `canvas`).

In `ApplyInpaintStroke` (Inpainting.cs) replace `maskLayer.Bitmap.Width/Height` with `_layers!.Width` / `_layers.Height` (the mask is canvas-sized, so this is a no-op today but keeps the rule explicit). If `_layers` can be null there, guard: `if (_layers is null) return false;` before use.

In `ApplyBackgroundMaskWithLayers` (BackgroundOps.cs): compute `var offset = _layers?.ActiveLayer is { } al ? new SKPointI(al.OffsetX, al.OffsetY) : default;` before creating layers, then pass it: `_services?.Layers.AddLayerFromBitmap(backgroundBitmap, "Background", offset);` and `..."Subject", offset)`. (The non-layer-mode branch keeps `default`.)

In `LoadLayeredTiff` (ImageEditorCore.cs) replace the block from `// Initialize layer mode from the loaded stack` through the `for` loop with:

```csharp
            // Rebuild the stack at the file's canvas size; every page keeps its own offset and size.
            _services.Layers.EnableLayerMode(loadedLayers.Width, loadedLayers.Height);
            for (var i = 0; i < loadedLayers.Count; i++)
            {
                var layer = loadedLayers[i];
                if (layer.Bitmap is null) continue;
                var added = _services.Layers.AddLayerFromBitmap(layer.Bitmap.Copy(), layer.Name, new SKPointI(layer.OffsetX, layer.OffsetY));
                if (added is not null)
                {
                    added.Opacity = layer.Opacity;
                    added.BlendMode = layer.BlendMode;
                    added.IsVisible = layer.IsVisible;
                }
            }
```

Remove the now-unused `firstLayer` null check only if `firstLayer` is not used elsewhere in the method (it is used for `.Bitmap is null` guard — keep the guard).

- [ ] **Step 4: Run tests** — filters `ImageEditorCoreOffsetLayerTests`, `ImageEditorCoreInpaintBaseTests`, `ShapeToolArrowTests`, `ImageEditorCoreSaveFormatTests`. Expected: green.

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/ImageEditor DiffusionNexus.Tests/ImageEditor/ImageEditorCoreOffsetLayerTests.cs
git commit -m "feat(editor): pixel tools address offset layers in canvas coordinates

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 5: Layered TIFF persists offsets and canvas size

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/TiffExporter.cs`
- Test: `DiffusionNexus.Tests/ImageEditor/TiffExporterOffsetTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>Layered TIFF round-trips layer offsets, mixed layer sizes and the canvas size.</summary>
public class TiffExporterOffsetTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dn-tiff-{Guid.NewGuid():N}.tif");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void SaveAndLoad_KeepsOffsetsSizesAndCanvas()
    {
        using var stack = new LayerStack(40, 30);
        using (var bg = new SKBitmap(40, 30)) { bg.Erase(SKColors.Red); stack.AddLayerFromBitmap(bg, "bg"); }
        using (var small = new SKBitmap(8, 6)) { small.Erase(SKColors.Blue); stack.AddLayerFromBitmap(small, "moved", new SKPointI(-3, 27)); }

        TiffExporter.SaveLayeredTiff(stack, _path).Should().BeTrue();
        using var loaded = TiffExporter.LoadLayeredTiff(_path)!;

        loaded.Width.Should().Be(40);
        loaded.Height.Should().Be(30);
        loaded.Count.Should().Be(2);
        loaded[1].Name.Should().Be("moved");
        loaded[1].Bounds.Should().Be(new SKRectI(-3, 27, 5, 33));
        loaded[1].Bitmap!.GetPixel(0, 0).Should().Be(SKColors.Blue);
    }

    [Fact]
    public void Load_WithoutOffsetKeys_UsesFirstPageSize_AndZeroOffsets()
    {
        using var stack = new LayerStack(16, 12);
        using (var bg = new SKBitmap(16, 12)) { bg.Erase(SKColors.Green); stack.AddLayerFromBitmap(bg, "bg"); }
        TiffExporter.SaveLayeredTiff(stack, _path).Should().BeTrue();
        // Legacy behaviour is the offset-0 special case; the file above carries the new keys, so
        // this test only proves the defaults are what the parser falls back to.
        TiffExporter.ParseLayerMetadataForTest("LayerName=x|Opacity=1.00|BlendMode=Normal|Visible=True|Index=0")
            .Should().Be(("x", 0, 0, (int?)null, (int?)null));
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `TiffExporterOffsetTests`. Expected: build error (`ParseLayerMetadataForTest`), then bounds mismatch.

- [ ] **Step 3: Implement**

`CreateLayerMetadata`:

```csharp
    private static string CreateLayerMetadata(Layer layer, int index, int canvasWidth, int canvasHeight)
    {
        return $"LayerName={layer.Name}|Opacity={layer.Opacity:F2}|BlendMode={layer.BlendMode}|Visible={layer.IsVisible}|Index={index}"
             + $"|OffsetX={layer.OffsetX}|OffsetY={layer.OffsetY}|CanvasWidth={canvasWidth}|CanvasHeight={canvasHeight}";
    }
```

Update its caller in `SaveLayeredTiff`: `CreateLayerMetadata(layer, pageIndex, layers.Width, layers.Height)`.

`ParseLayerMetadata` gains `out int offsetX, out int offsetY, out int? canvasWidth, out int? canvasHeight` (defaults 0, 0, null, null) with cases:

```csharp
                case "OffsetX": if (int.TryParse(value, out var ox)) offsetX = ox; break;
                case "OffsetY": if (int.TryParse(value, out var oy)) offsetY = oy; break;
                case "CanvasWidth": if (int.TryParse(value, out var cw) && cw > 0) canvasWidth = cw; break;
                case "CanvasHeight": if (int.TryParse(value, out var ch) && ch > 0) canvasHeight = ch; break;
```

Add the test seam:

```csharp
    /// <summary>Test seam: name, offset and canvas size parsed from a page description.</summary>
    internal static (string Name, int OffsetX, int OffsetY, int? CanvasWidth, int? CanvasHeight) ParseLayerMetadataForTest(string metadata)
    {
        ParseLayerMetadata(metadata, out var name, out _, out _, out _, out var ox, out var oy, out var cw, out var ch);
        return (name, ox, oy, cw, ch);
    }
```

(`DiffusionNexus.UI` already has `InternalsVisibleTo` for the test project — verify with `grep -rn InternalsVisibleTo DiffusionNexus.UI/*.csproj DiffusionNexus.UI/Properties 2>/dev/null`; if not, make the seam `public`.)

`LoadLayeredTiff`: read the first page's description **before** creating the stack:

```csharp
            var firstWidth = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            var firstHeight = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            var firstDescription = tiff.GetField(TiffTag.IMAGEDESCRIPTION);
            int? canvasW = null, canvasH = null;
            if (firstDescription != null && firstDescription.Length > 0)
                ParseLayerMetadata(firstDescription[0].ToString(), out _, out _, out _, out _, out _, out _, out canvasW, out canvasH);
            var layerStack = new LayerStack(canvasW ?? firstWidth, canvasH ?? firstHeight);
```

Inside the loop parse with the new outs and construct `new Layer(bitmap, layerName, new SKPointI(offsetX, offsetY))`.

- [ ] **Step 4: Run tests** — filters `TiffExporterOffsetTests`, `ImageEditorCoreSaveFormatTests`. Expected: green.

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/ImageEditor/TiffExporter.cs DiffusionNexus.Tests/ImageEditor/TiffExporterOffsetTests.cs
git commit -m "feat(editor): layered TIFF stores layer offsets and the canvas size

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 6: `LayerTransformTool` — state, matrix, setters, hit test, gestures, render

**Files:**
- Create: `DiffusionNexus.UI/ImageEditor/LayerTransformTool.cs`
- Test: `DiffusionNexus.Tests/ImageEditor/LayerTransformToolTests.cs` (create)

**Interfaces:**
- Consumes: `Layer.Bounds`, `Layer.Bitmap` (Task 1).
- Produces (used by Tasks 7, 10, 11):
  - `public enum TransformHandle { None, Body, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Rotate }`
  - `public sealed class LayerTransformTool` with: `bool IsActive`, `bool IsArmed`, `Layer? Layer`, `SKRectI SourceBounds`, `void Arm(Layer layer)`, `void Disarm()`, `void SetImageBounds(SKRect imageRect)`, `int ImagePixelWidth`, `int ImagePixelHeight`, `float Scale`, `SKPoint Translation`, `float ScaleX`, `float ScaleY`, `float RotationDegrees`, `bool KeepAspect`, `bool ConstrainProportionsOverride`, `bool SnapRotation`, `SKMatrix Matrix`, `SKRect TransformedBounds`, `bool HasTransform`, `bool IsDragging`, `TransformHandle HitTest(SKPoint screenPoint)`, `TransformHandle GetCursorForPoint(SKPoint screenPoint)`, `bool OnPointerPressed(SKPoint)`, `bool OnPointerMoved(SKPoint)`, `bool OnPointerReleased()`, `void SetPosition(float x, float y)`, `void SetSize(float w, float h)`, `void SetRotation(float degrees)`, `void FlipHorizontal()`, `void FlipVertical()`, `void Nudge(int dx, int dy)`, `void Reset()`, `bool Commit()`, `void Render(SKCanvas canvas, SKRect canvasBounds)`, events `TransformChanged`, `CommitRequested`, `ArmedLayerChanged`.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// The Move / Transform tool: Shape-style handles on the active layer's bounds, body drag moves,
/// corners and edges scale, the top handle rotates; the state is one canvas-space matrix that
/// is never applied to pixels until Commit.
/// </summary>
public class LayerTransformToolTests : IDisposable
{
    // Canvas 200x100 shown 1:1 at screen origin (Scale = 1) so canvas px == screen px.
    private readonly Layer _layer = new(40, 20, "L");
    private readonly LayerTransformTool _sut = new() { IsActive = true, ImagePixelWidth = 200, ImagePixelHeight = 100 };

    public LayerTransformToolTests()
    {
        _sut.SetImageBounds(new SKRect(0, 0, 200, 100));
        _layer.SetOffset(60, 40); // bounds (60,40)-(100,60), centre (80,50)
        _sut.Arm(_layer);
    }

    public void Dispose() => _layer.Dispose();

    [Fact]
    public void Arm_CapturesSourceBounds_AndIsIdentity()
    {
        _sut.IsArmed.Should().BeTrue();
        _sut.SourceBounds.Should().Be(new SKRectI(60, 40, 100, 60));
        _sut.HasTransform.Should().BeFalse();
        _sut.TransformedBounds.Should().Be(new SKRect(60, 40, 100, 60));
    }

    [Fact]
    public void HitTest_FindsCornersEdgesRotateAndBody_InOrder()
    {
        _sut.HitTest(new SKPoint(60, 40)).Should().Be(TransformHandle.TopLeft);
        _sut.HitTest(new SKPoint(80, 40)).Should().Be(TransformHandle.Top);
        _sut.HitTest(new SKPoint(100, 50)).Should().Be(TransformHandle.Right);
        _sut.HitTest(new SKPoint(100, 60)).Should().Be(TransformHandle.BottomRight);
        _sut.HitTest(new SKPoint(80, 40 - 30)).Should().Be(TransformHandle.Rotate);
        _sut.HitTest(new SKPoint(80, 50)).Should().Be(TransformHandle.Body);
        _sut.HitTest(new SKPoint(150, 90)).Should().Be(TransformHandle.None);
        _sut.HitTest(new SKPoint(60 - 11, 40)).Should().Be(TransformHandle.TopLeft);   // inside hit radius 12
        _sut.HitTest(new SKPoint(60 - 13, 40 - 13)).Should().Be(TransformHandle.None);
    }

    [Fact]
    public void BodyDrag_Translates_AndRaisesTransformChanged()
    {
        var changed = 0;
        _sut.TransformChanged += (_, _) => changed++;

        _sut.OnPointerPressed(new SKPoint(80, 50)).Should().BeTrue();
        _sut.OnPointerMoved(new SKPoint(90, 55)).Should().BeTrue();
        _sut.OnPointerReleased().Should().BeTrue();

        _sut.Translation.Should().Be(new SKPoint(10, 5));
        _sut.TransformedBounds.Should().Be(new SKRect(70, 45, 110, 65));
        _sut.HasTransform.Should().BeTrue();
        changed.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CornerDrag_WithoutAspect_ScalesAboutOppositeCorner()
    {
        _sut.KeepAspect = false;
        _sut.OnPointerPressed(new SKPoint(100, 60)); // bottom-right
        _sut.OnPointerMoved(new SKPoint(120, 80));   // +20, +20 => 60x40
        _sut.OnPointerReleased();

        _sut.TransformedBounds.Left.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Top.Should().BeApproximately(40, 0.01f);
        _sut.TransformedBounds.Width.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Height.Should().BeApproximately(40, 0.01f);
    }

    [Fact]
    public void CornerDrag_WithAspect_UsesUniformFactor()
    {
        _sut.KeepAspect = true;
        _sut.OnPointerPressed(new SKPoint(100, 60));
        _sut.OnPointerMoved(new SKPoint(120, 60)); // width factor 1.5, height factor 1 => 1.5 both
        _sut.OnPointerReleased();

        _sut.TransformedBounds.Width.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Height.Should().BeApproximately(30, 0.01f);
        _sut.TransformedBounds.Left.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Top.Should().BeApproximately(40, 0.01f);
    }

    [Fact]
    public void CtrlOverride_InvertsKeepAspectForTheDrag()
    {
        _sut.KeepAspect = true;
        _sut.ConstrainProportionsOverride = true; // Ctrl held => free scaling
        _sut.OnPointerPressed(new SKPoint(100, 60));
        _sut.OnPointerMoved(new SKPoint(120, 60));
        _sut.OnPointerReleased();

        _sut.TransformedBounds.Width.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Height.Should().BeApproximately(20, 0.01f);
    }

    [Fact]
    public void EdgeDrag_ScalesOneAxisAboutOppositeEdge()
    {
        _sut.OnPointerPressed(new SKPoint(100, 50)); // right edge
        _sut.OnPointerMoved(new SKPoint(140, 50));
        _sut.OnPointerReleased();

        _sut.TransformedBounds.Should().Be(new SKRect(60, 40, 140, 60));
    }

    [Fact]
    public void RotateDrag_SetsAngle_AndShiftSnapsToFifteen()
    {
        _sut.OnPointerPressed(new SKPoint(80, 10)); // rotate handle (centre 80,50 => angle -90°)
        _sut.OnPointerMoved(new SKPoint(120, 50)); // angle 0° => +90°
        _sut.OnPointerReleased();
        _sut.RotationDegrees.Should().BeApproximately(90f, 0.01f);

        _sut.Reset();
        _sut.SnapRotation = true;
        _sut.OnPointerPressed(new SKPoint(80, 10));
        _sut.OnPointerMoved(new SKPoint(80 + 40 * MathF.Cos(-1.1f), 50 + 40 * MathF.Sin(-1.1f))); // ~27°
        _sut.OnPointerReleased();
        _sut.RotationDegrees.Should().BeApproximately(30f, 0.01f);
    }

    [Fact]
    public void RotatedBox_HitTestsInLocalSpace()
    {
        _sut.SetRotation(90f); // 40x20 box becomes 20 wide, 40 tall around (80,50)
        _sut.HitTest(new SKPoint(80, 30)).Should().NotBe(TransformHandle.None); // top of rotated box
        _sut.HitTest(new SKPoint(80 + 30, 50)).Should().Be(TransformHandle.Rotate); // rotate handle swings to the right
    }

    [Fact]
    public void Flips_NegateScale_AndKeepCentre()
    {
        _sut.FlipHorizontal();
        _sut.ScaleX.Should().Be(-1f);
        _sut.TransformedBounds.Should().Be(new SKRect(60, 40, 100, 60));
        _sut.HasTransform.Should().BeTrue();
        _sut.FlipVertical();
        _sut.ScaleY.Should().Be(-1f);
    }

    [Fact]
    public void SetPositionSizeRotation_RoundTrip()
    {
        _sut.SetPosition(10, 20);
        _sut.TransformedBounds.Left.Should().BeApproximately(10, 0.01f);
        _sut.TransformedBounds.Top.Should().BeApproximately(20, 0.01f);

        _sut.KeepAspect = false;
        _sut.SetSize(80, 10);
        _sut.TransformedBounds.Width.Should().BeApproximately(80, 0.01f);
        _sut.TransformedBounds.Height.Should().BeApproximately(10, 0.01f);
        _sut.TransformedBounds.Left.Should().BeApproximately(10, 0.01f); // size keeps top-left

        _sut.KeepAspect = true;
        _sut.SetSize(40, 999); // width wins, height follows the source aspect (2:1)
        _sut.TransformedBounds.Height.Should().BeApproximately(20, 0.01f);

        _sut.SetRotation(45f);
        _sut.RotationDegrees.Should().Be(45f);
    }

    [Fact]
    public void Nudge_MovesByCanvasPixels()
    {
        _sut.Nudge(1, 0);
        _sut.Nudge(0, -10);
        _sut.Translation.Should().Be(new SKPoint(1, -10));
    }

    [Fact]
    public void HasTransform_IgnoresSubPixelNoise()
    {
        _sut.Nudge(0, 0);
        _sut.HasTransform.Should().BeFalse();
        _sut.SetRotation(0.0001f);
        _sut.HasTransform.Should().BeFalse();
    }

    [Fact]
    public void Commit_RaisesOnlyWithTransform_AndResetClearsState()
    {
        var commits = 0;
        _sut.CommitRequested += (_, _) => commits++;

        _sut.Commit().Should().BeFalse();
        commits.Should().Be(0);

        _sut.Nudge(5, 0);
        _sut.Commit().Should().BeTrue();
        commits.Should().Be(1);

        _sut.Nudge(5, 0);
        _sut.Reset();
        _sut.HasTransform.Should().BeFalse();
        _sut.IsArmed.Should().BeTrue(); // Reset keeps the layer armed
    }

    [Fact]
    public void DeactivatingWithPendingTransform_CommitsThenDisarms()
    {
        var commits = 0;
        _sut.CommitRequested += (_, _) => commits++;
        _sut.Nudge(3, 3);

        _sut.IsActive = false;

        commits.Should().Be(1);
        _sut.IsArmed.Should().BeFalse();
    }

    [Fact]
    public void Scale_MapsScreenToCanvas()
    {
        _sut.SetImageBounds(new SKRect(0, 0, 400, 200)); // 2 screen px per canvas px
        _sut.Scale.Should().Be(2f);
        _sut.OnPointerPressed(new SKPoint(160, 100)); // body (canvas 80,50)
        _sut.OnPointerMoved(new SKPoint(180, 100));
        _sut.OnPointerReleased();
        _sut.Translation.Should().Be(new SKPoint(10, 0));
    }

    [Fact]
    public void Render_DoesNotThrow_WhileArmedAndTransformed()
    {
        using var bmp = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bmp);
        _sut.Nudge(150, 0); // pushes part of the layer off canvas => dimmed preview path runs
        _sut.SetRotation(20f);
        var act = () => _sut.Render(canvas, new SKRect(0, 0, 200, 100));
        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `LayerTransformToolTests`. Expected: build error, `LayerTransformTool` missing.

- [ ] **Step 3: Implement `LayerTransformTool.cs`**

```csharp
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>Which part of the transform box the pointer is on.</summary>
public enum TransformHandle
{
    None, Body, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Rotate
}

/// <summary>
/// Move / Transform tool for the active layer. Holds a translation, scale (sign = flip) and
/// rotation about the centre of the layer's bounds at arm time and exposes them as one
/// canvas-space <see cref="Matrix"/>. Never touches pixels: the compositor previews the matrix,
/// <c>ImageEditorCore.ApplyLayerTransform</c> rasterizes it on <see cref="Commit"/>.
/// Handle vocabulary and geometry follow <see cref="ShapeTool"/>.
/// </summary>
public sealed class LayerTransformTool
{
    private enum Phase { Idle, Moving, Scaling, Rotating }

    public const float HandleRadius = 6f;
    public const float HandleHitRadius = 12f;
    public const float RotateHandleOffset = 30f;
    public const float RotationSnapDegrees = 15f;

    private bool _isActive;
    private Layer? _layer;
    private SKRectI _sourceBounds;
    private SKRect _imageRect;
    private Phase _phase = Phase.Idle;
    private TransformHandle _activeHandle;

    private SKPoint _translation;
    private float _scaleX = 1f;
    private float _scaleY = 1f;
    private float _rotation;

    // Drag bookkeeping (canvas px unless noted)
    private SKPoint _dragStartCanvas;
    private SKPoint _translationAtPress;
    private float _scaleXAtPress, _scaleYAtPress;
    private float _rotationAtPress;
    private float _pressAngleDegrees;
    private bool _aspectForThisDrag;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (!value)
            {
                Commit();           // a deliberate move is never lost silently (Shape/Text precedent)
                Disarm();
            }
        }
    }

    public bool IsArmed => _layer is not null;
    public Layer? Layer => _layer;
    public SKRectI SourceBounds => _sourceBounds;
    public int ImagePixelWidth { get; set; }
    public int ImagePixelHeight { get; set; }
    /// <summary>Screen px per canvas px.</summary>
    public float Scale => ImagePixelWidth > 0 && _imageRect.Width > 0 ? _imageRect.Width / ImagePixelWidth : 1f;

    public SKPoint Translation => _translation;
    public float ScaleX => _scaleX;
    public float ScaleY => _scaleY;
    public float RotationDegrees => _rotation;
    public bool KeepAspect { get; set; } = true;
    /// <summary>Ctrl state from the control: inverts <see cref="KeepAspect"/> for a corner drag.</summary>
    public bool ConstrainProportionsOverride { get; set; }
    /// <summary>Shift state from the control: snap rotation to 15°.</summary>
    public bool SnapRotation { get; set; }
    public bool IsDragging => _phase != Phase.Idle;

    public event EventHandler? TransformChanged;
    public event EventHandler? CommitRequested;
    public event EventHandler? ArmedLayerChanged;

    private SKPoint Pivot => new(_sourceBounds.MidX, _sourceBounds.MidY);

    /// <summary>Canvas-space transform of the source bounds.</summary>
    public SKMatrix Matrix
    {
        get
        {
            var c = Pivot;
            var m = SKMatrix.CreateTranslation(-c.X, -c.Y);
            m = m.PostConcat(SKMatrix.CreateScale(_scaleX, _scaleY));
            m = m.PostConcat(SKMatrix.CreateRotationDegrees(_rotation));
            m = m.PostConcat(SKMatrix.CreateTranslation(c.X + _translation.X, c.Y + _translation.Y));
            return m;
        }
    }

    public SKRect TransformedBounds => Matrix.MapRect(SKRect.Create(_sourceBounds.Left, _sourceBounds.Top, _sourceBounds.Width, _sourceBounds.Height));

    /// <summary>Axis-aligned box before rotation (what the handles sit on), canvas px.</summary>
    private SKRect UnrotatedBox
    {
        get
        {
            var c = Pivot;
            var w = _sourceBounds.Width * MathF.Abs(_scaleX);
            var h = _sourceBounds.Height * MathF.Abs(_scaleY);
            var cx = c.X + _translation.X;
            var cy = c.Y + _translation.Y;
            return new SKRect(cx - w / 2f, cy - h / 2f, cx + w / 2f, cy + h / 2f);
        }
    }

    public bool HasTransform =>
        MathF.Abs(_translation.X) > 0.5f || MathF.Abs(_translation.Y) > 0.5f ||
        MathF.Abs(_scaleX - 1f) > 1e-3f || MathF.Abs(_scaleY - 1f) > 1e-3f ||
        MathF.Abs(_rotation) > 1e-3f;

    public void Arm(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _layer = layer;
        _sourceBounds = layer.Bounds;
        ResetState();
        ArmedLayerChanged?.Invoke(this, EventArgs.Empty);
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Disarm()
    {
        if (_layer is null) return;
        _layer = null;
        ResetState();
        ArmedLayerChanged?.Invoke(this, EventArgs.Empty);
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetImageBounds(SKRect imageRect) => _imageRect = imageRect;

    /// <summary>Back to identity; the layer stays armed.</summary>
    public void Reset()
    {
        ResetState();
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetState()
    {
        _translation = SKPoint.Empty;
        _scaleX = _scaleY = 1f;
        _rotation = 0f;
        _phase = Phase.Idle;
        _activeHandle = TransformHandle.None;
    }

    /// <summary>Raises <see cref="CommitRequested"/> when there is something to apply.</summary>
    public bool Commit()
    {
        if (_layer is null || !HasTransform) return false;
        CommitRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    #region Coordinate mapping

    private SKPoint ScreenToCanvas(SKPoint p) => new((p.X - _imageRect.Left) / Scale, (p.Y - _imageRect.Top) / Scale);
    private SKPoint CanvasToScreen(SKPoint p) => new(_imageRect.Left + p.X * Scale, _imageRect.Top + p.Y * Scale);

    private static SKPoint RotatePointAround(SKPoint point, SKPoint center, float degrees)
    {
        var rad = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return new SKPoint(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    private static float DistanceSq(SKPoint a, SKPoint b) { var dx = a.X - b.X; var dy = a.Y - b.Y; return dx * dx + dy * dy; }

    /// <summary>Screen position of a handle on the rotated box.</summary>
    private SKPoint HandleScreenPoint(TransformHandle handle)
    {
        var box = UnrotatedBox;
        var centre = new SKPoint(box.MidX, box.MidY);
        var local = handle switch
        {
            TransformHandle.TopLeft => new SKPoint(box.Left, box.Top),
            TransformHandle.Top => new SKPoint(box.MidX, box.Top),
            TransformHandle.TopRight => new SKPoint(box.Right, box.Top),
            TransformHandle.Right => new SKPoint(box.Right, box.MidY),
            TransformHandle.BottomRight => new SKPoint(box.Right, box.Bottom),
            TransformHandle.Bottom => new SKPoint(box.MidX, box.Bottom),
            TransformHandle.BottomLeft => new SKPoint(box.Left, box.Bottom),
            TransformHandle.Left => new SKPoint(box.Left, box.MidY),
            TransformHandle.Rotate => new SKPoint(box.MidX, box.Top - RotateHandleOffset / Scale),
            _ => centre
        };
        return CanvasToScreen(RotatePointAround(local, centre, _rotation));
    }

    #endregion

    #region Hit testing

    private static readonly TransformHandle[] HitOrder =
    [
        TransformHandle.TopLeft, TransformHandle.TopRight, TransformHandle.BottomLeft, TransformHandle.BottomRight,
        TransformHandle.Top, TransformHandle.Right, TransformHandle.Bottom, TransformHandle.Left,
        TransformHandle.Rotate
    ];

    public TransformHandle HitTest(SKPoint screenPoint)
    {
        if (_layer is null) return TransformHandle.None;

        foreach (var h in HitOrder)
        {
            var r = h == TransformHandle.Rotate ? HandleHitRadius + 6f : HandleHitRadius;
            if (DistanceSq(screenPoint, HandleScreenPoint(h)) <= r * r) return h;
        }

        // Body: rotate the pointer into the box's local space (canvas px) and test the box.
        var box = UnrotatedBox;
        var centre = new SKPoint(box.MidX, box.MidY);
        var local = RotatePointAround(ScreenToCanvas(screenPoint), centre, -_rotation);
        return box.Contains(local) ? TransformHandle.Body : TransformHandle.None;
    }

    public TransformHandle GetCursorForPoint(SKPoint screenPoint) => HitTest(screenPoint);

    #endregion

    #region Gestures

    public bool OnPointerPressed(SKPoint screenPoint)
    {
        if (!_isActive || _layer is null) return false;

        var handle = HitTest(screenPoint);
        if (handle == TransformHandle.None) return false;

        _activeHandle = handle;
        _dragStartCanvas = ScreenToCanvas(screenPoint);
        _translationAtPress = _translation;
        _scaleXAtPress = _scaleX;
        _scaleYAtPress = _scaleY;
        _rotationAtPress = _rotation;
        _aspectForThisDrag = KeepAspect ^ ConstrainProportionsOverride;

        var box = UnrotatedBox;
        var centre = new SKPoint(box.MidX, box.MidY);
        _pressAngleDegrees = MathF.Atan2(_dragStartCanvas.Y - centre.Y, _dragStartCanvas.X - centre.X) * 180f / MathF.PI;

        _phase = handle switch
        {
            TransformHandle.Body => Phase.Moving,
            TransformHandle.Rotate => Phase.Rotating,
            _ => Phase.Scaling
        };
        return true;
    }

    public bool OnPointerMoved(SKPoint screenPoint)
    {
        if (!_isActive || _layer is null || _phase == Phase.Idle) return false;

        var canvasPoint = ScreenToCanvas(screenPoint);
        switch (_phase)
        {
            case Phase.Moving:
                _translation = new SKPoint(
                    _translationAtPress.X + (canvasPoint.X - _dragStartCanvas.X),
                    _translationAtPress.Y + (canvasPoint.Y - _dragStartCanvas.Y));
                break;
            case Phase.Scaling:
                ApplyScaleDrag(canvasPoint);
                break;
            case Phase.Rotating:
                ApplyRotateDrag(canvasPoint);
                break;
        }

        TransformChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool OnPointerReleased()
    {
        if (_phase == Phase.Idle) return false;
        _phase = Phase.Idle;
        _activeHandle = TransformHandle.None;
        TransformChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ApplyScaleDrag(SKPoint canvasPoint)
    {
        // Work in the box's local (unrotated) space around the centre at press time.
        var c = Pivot;
        var centreAtPress = new SKPoint(c.X + _translationAtPress.X, c.Y + _translationAtPress.Y);
        var localStart = RotatePointAround(_dragStartCanvas, centreAtPress, -_rotationAtPress);
        var localNow = RotatePointAround(canvasPoint, centreAtPress, -_rotationAtPress);

        var w0 = _sourceBounds.Width * MathF.Abs(_scaleXAtPress);
        var h0 = _sourceBounds.Height * MathF.Abs(_scaleYAtPress);
        var box = new SKRect(centreAtPress.X - w0 / 2f, centreAtPress.Y - h0 / 2f, centreAtPress.X + w0 / 2f, centreAtPress.Y + h0 / 2f);

        // Anchor = the opposite corner/edge; the dragged side follows the pointer.
        var left = box.Left; var top = box.Top; var right = box.Right; var bottom = box.Bottom;
        var dx = localNow.X - localStart.X;
        var dy = localNow.Y - localStart.Y;
        var movesLeft = _activeHandle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft;
        var movesRight = _activeHandle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight;
        var movesTop = _activeHandle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight;
        var movesBottom = _activeHandle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight;
        if (movesLeft) left += dx;
        if (movesRight) right += dx;
        if (movesTop) top += dy;
        if (movesBottom) bottom += dy;

        var newW = right - left;
        var newH = bottom - top;
        var fx = w0 > 0 ? newW / w0 : 1f;
        var fy = h0 > 0 ? newH / h0 : 1f;

        var isCorner = _activeHandle is TransformHandle.TopLeft or TransformHandle.TopRight or TransformHandle.BottomLeft or TransformHandle.BottomRight;
        if (isCorner && _aspectForThisDrag)
        {
            var f = MathF.Abs(fx) >= MathF.Abs(fy) ? fx : fy;
            fx = f; fy = f;
            newW = w0 * fx; newH = h0 * fy;
            // Re-anchor the box on the opposite corner.
            if (movesLeft) left = right - newW; else right = left + newW;
            if (movesTop) top = bottom - newH; else bottom = top + newH;
        }

        const float minFactor = 0.01f;
        if (MathF.Abs(fx) < minFactor || MathF.Abs(fy) < minFactor) return;

        _scaleX = MathF.Sign(_scaleXAtPress) * MathF.Abs(_scaleXAtPress) * fx;
        _scaleY = MathF.Sign(_scaleYAtPress) * MathF.Abs(_scaleYAtPress) * fy;

        // New centre in local space, rotated back into canvas space => translation.
        var newLocalCentre = new SKPoint((left + right) / 2f, (top + bottom) / 2f);
        var newCentre = RotatePointAround(newLocalCentre, centreAtPress, _rotationAtPress);
        _translation = new SKPoint(newCentre.X - c.X, newCentre.Y - c.Y);
    }

    private void ApplyRotateDrag(SKPoint canvasPoint)
    {
        var box = UnrotatedBox;
        var centre = new SKPoint(box.MidX, box.MidY);
        var angle = MathF.Atan2(canvasPoint.Y - centre.Y, canvasPoint.X - centre.X) * 180f / MathF.PI;
        var rotation = _rotationAtPress + (angle - _pressAngleDegrees);
        if (SnapRotation) rotation = MathF.Round(rotation / RotationSnapDegrees) * RotationSnapDegrees;
        _rotation = NormalizeDegrees(rotation);
    }

    private static float NormalizeDegrees(float d)
    {
        d %= 360f;
        if (d > 180f) d -= 360f;
        if (d <= -180f) d += 360f;
        return d;
    }

    #endregion

    #region Panel setters

    /// <summary>Top-left of <see cref="TransformedBounds"/> in canvas px.</summary>
    public void SetPosition(float x, float y)
    {
        var b = TransformedBounds;
        _translation = new SKPoint(_translation.X + (x - b.Left), _translation.Y + (y - b.Top));
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Size of the unrotated box in canvas px, keeping its top-left where it is. With
    /// <see cref="KeepAspect"/> the width wins and the height follows the source aspect.
    /// </summary>
    public void SetSize(float w, float h)
    {
        if (w < 1f || h < 1f || _sourceBounds.Width == 0 || _sourceBounds.Height == 0) return;
        var before = TransformedBounds;
        var fx = w / _sourceBounds.Width;
        var fy = KeepAspect ? fx : h / _sourceBounds.Height;
        _scaleX = MathF.Sign(_scaleX) * fx;
        _scaleY = MathF.Sign(_scaleY) * fy;
        var after = TransformedBounds;
        _translation = new SKPoint(_translation.X + (before.Left - after.Left), _translation.Y + (before.Top - after.Top));
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRotation(float degrees)
    {
        _rotation = NormalizeDegrees(degrees);
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FlipHorizontal() { _scaleX = -_scaleX; TransformChanged?.Invoke(this, EventArgs.Empty); }
    public void FlipVertical() { _scaleY = -_scaleY; TransformChanged?.Invoke(this, EventArgs.Empty); }

    public void Nudge(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        _translation = new SKPoint(_translation.X + dx, _translation.Y + dy);
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Rendering

    /// <summary>
    /// Off-canvas part of the preview at 50 % (the compositor draws the in-canvas part), the
    /// dashed rotated box, eight scale handles, the rotate handle with its stem, and a size /
    /// angle label under the box.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect canvasBounds)
    {
        if (!_isActive || _layer?.Bitmap is null) return;

        var s = Scale;
        // canvas px -> screen px
        var toScreen = SKMatrix.CreateScale(s, s).PostConcat(SKMatrix.CreateTranslation(_imageRect.Left, _imageRect.Top));

        if (HasTransform)
        {
            canvas.Save();
            canvas.ClipRect(_imageRect, SKClipOperation.Difference);
            canvas.Concat(toScreen);
            canvas.Concat(Matrix);
            using var dim = new SKPaint { Color = SKColors.White.WithAlpha(128), IsAntialias = true };
            canvas.DrawBitmap(_layer.Bitmap, _layer.OffsetX, _layer.OffsetY, dim);
            canvas.Restore();
        }

        var box = UnrotatedBox;
        var centre = CanvasToScreen(new SKPoint(box.MidX, box.MidY));
        var screenBox = new SKRect(
            centre.X - box.Width * s / 2f, centre.Y - box.Height * s / 2f,
            centre.X + box.Width * s / 2f, centre.Y + box.Height * s / 2f);

        canvas.Save();
        canvas.RotateDegrees(_rotation, centre.X, centre.Y);

        using var linePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f,
            IsAntialias = true, PathEffect = SKPathEffect.CreateDash([6f, 4f], 0)
        };
        canvas.DrawRect(screenBox, linePaint);

        using var handleFill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var handleStroke = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        SKPoint[] handles =
        [
            new(screenBox.Left, screenBox.Top), new(screenBox.MidX, screenBox.Top), new(screenBox.Right, screenBox.Top),
            new(screenBox.Right, screenBox.MidY), new(screenBox.Right, screenBox.Bottom), new(screenBox.MidX, screenBox.Bottom),
            new(screenBox.Left, screenBox.Bottom), new(screenBox.Left, screenBox.MidY)
        ];
        foreach (var h in handles)
        {
            canvas.DrawCircle(h, HandleRadius, handleFill);
            canvas.DrawCircle(h, HandleRadius, handleStroke);
        }

        var topCentre = new SKPoint(screenBox.MidX, screenBox.Top);
        var rotateCentre = new SKPoint(screenBox.MidX, screenBox.Top - RotateHandleOffset);
        using var stemPaint = new SKPaint { Color = new SKColor(255, 255, 255, 140), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        canvas.DrawLine(topCentre, rotateCentre, stemPaint);
        using var rotateBg = new SKPaint { Color = new SKColor(60, 60, 60, 220), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(rotateCentre, 12f, rotateBg);
        canvas.DrawCircle(rotateCentre, 12f, handleStroke);
        using var arcPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawArc(new SKRect(rotateCentre.X - 6f, rotateCentre.Y - 6f, rotateCentre.X + 6f, rotateCentre.Y + 6f), -220f, 260f, false, arcPaint);

        canvas.Restore();

        DrawLabel(canvas, screenBox, centre);
    }

    private void DrawLabel(SKCanvas canvas, SKRect screenBox, SKPoint centre)
    {
        var b = TransformedBounds;
        var text = MathF.Abs(_rotation) > 1e-3f
            ? $"{MathF.Round(b.Width)} x {MathF.Round(b.Height)}  ·  {_rotation:0.#}°"
            : $"{MathF.Round(b.Width)} x {MathF.Round(b.Height)}";

        using var font = new SKFont(SKTypeface.Default, 12f);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        font.MeasureText(text, out var textBounds, textPaint);

        var halfDiag = MathF.Sqrt(screenBox.Width * screenBox.Width + screenBox.Height * screenBox.Height) / 2f;
        var labelX = centre.X - textBounds.Width / 2f;
        var labelY = centre.Y + halfDiag + 8f + textBounds.Height;

        var bgRect = new SKRect(labelX - 6f, labelY - textBounds.Height - 2f, labelX + textBounds.Width + 6f, labelY + 4f);
        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(bgRect, 4f, 4f, bgPaint);
        canvas.DrawText(text, labelX, labelY, font, textPaint);
    }

    #endregion
}
```

- [ ] **Step 4: Run tests** — filter `LayerTransformToolTests`. Expected: 17 passed. If `CornerDrag_WithAspect_UsesUniformFactor` fails on the anchor, check that the re-anchor block runs before `_translation` is computed.

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/ImageEditor/LayerTransformTool.cs DiffusionNexus.Tests/ImageEditor/LayerTransformToolTests.cs
git commit -m "feat(editor): LayerTransformTool with Shape-style handles and a canvas-space matrix

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 7: `ImageEditorCore` — arm, preview, apply

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs` (tool property, `RenderWithZoom`, `ActiveLayer` setter, events)
- Create: `DiffusionNexus.UI/ImageEditor/ImageEditorCore.LayerTransform.cs` (partial)
- Test: `DiffusionNexus.Tests/ImageEditor/ImageEditorCoreLayerTransformTests.cs` (create)

**Interfaces:**
- Consumes: Task 6 tool, Task 2 `LayerRenderOverride`.
- Produces: `public LayerTransformTool LayerTransformTool { get; }`, `public enum LayerTransformEligibility { Ok, NoLayer, InpaintMask, Locked }`, `public enum LayerTransformFailure { TooLarge, Allocation }`, `public LayerTransformEligibility ArmLayerTransform()`, `public bool ApplyLayerTransform()`, events `LayerTransformApplied`, `LayerTransformFailed (EventHandler<LayerTransformFailure>)`, `LayerTransformEligibilityChanged (EventHandler<LayerTransformEligibility>)`. Constants `MaxTransformedSide = 16384`, `MaxTransformedArea = 268_435_456L`.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Committing a layer transform rasterizes once into a bitmap sized to the transformed bounds,
/// keeps off-canvas pixels, refuses ineligible layers and oversize results, and commits a
/// pending transform before the active layer changes.
/// </summary>
public class ImageEditorCoreLayerTransformTests : IDisposable
{
    private readonly ImageEditorCore _sut = new();
    private readonly EditorServices _services = EditorServiceFactory.Create();

    public ImageEditorCoreLayerTransformTests()
    {
        _sut.SetServices(_services);
        using var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray());
        _sut.LayerTransformTool.ImagePixelWidth = 100;
        _sut.LayerTransformTool.ImagePixelHeight = 100;
        _sut.LayerTransformTool.SetImageBounds(new SKRect(0, 0, 100, 100));
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public void Arm_EnablesLayerMode_AndArmsTheActiveLayer()
    {
        _sut.IsLayerMode.Should().BeFalse();
        _sut.LayerTransformTool.IsActive = true;

        _sut.ArmLayerTransform().Should().Be(LayerTransformEligibility.Ok);

        _sut.IsLayerMode.Should().BeTrue();
        _sut.LayerTransformTool.Layer.Should().BeSameAs(_sut.ActiveLayer);
    }

    [Fact]
    public void Arm_RefusesMaskAndLockedLayers()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.EnableLayerMode();
        var locked = _services.Layers.AddLayer("locked")!;
        locked.IsLocked = true;
        _sut.ActiveLayer = locked;
        _sut.ArmLayerTransform().Should().Be(LayerTransformEligibility.Locked);
        _sut.LayerTransformTool.IsArmed.Should().BeFalse();

        var mask = _services.Layers.AddLayer("mask")!;
        mask.IsInpaintMask = true;
        _sut.ActiveLayer = mask;
        _sut.ArmLayerTransform().Should().Be(LayerTransformEligibility.InpaintMask);
    }

    [Fact]
    public void IntegerMove_IsPixelExact_AndKeepsOffCanvasPixels()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        _sut.LayerTransformTool.Nudge(60, 0);

        _sut.ApplyLayerTransform().Should().BeTrue();

        layer.Bounds.Should().Be(new SKRectI(60, 0, 160, 100));
        layer.Bitmap!.Width.Should().Be(100);
        layer.Bitmap!.GetPixel(99, 50).Should().Be(SKColors.Red); // pixel that is now off canvas survives
        _sut.LayerTransformTool.HasTransform.Should().BeFalse(); // re-armed at identity
        _sut.LayerTransformTool.IsArmed.Should().BeTrue();
    }

    [Fact]
    public void Scale_ProducesTransformedSize()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        _sut.LayerTransformTool.KeepAspect = false;
        _sut.LayerTransformTool.SetSize(50, 25);

        _sut.ApplyLayerTransform().Should().BeTrue();

        layer.Bitmap!.Width.Should().Be(50);
        layer.Bitmap!.Height.Should().Be(25);
        layer.Bitmap!.GetPixel(25, 12).Red.Should().BeGreaterThan(200);
    }

    [Fact]
    public void Rotation_GrowsBoundsToTheRotatedBox()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        _sut.LayerTransformTool.SetRotation(45f);

        _sut.ApplyLayerTransform().Should().BeTrue();

        layer.Width.Should().BeInRange(141, 143); // 100 * sqrt2, rounded out
        layer.OffsetX.Should().BeInRange(-22, -20);
    }

    [Fact]
    public void TooLarge_IsRefused_AndLayerUntouched()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        LayerTransformFailure? failure = null;
        _sut.LayerTransformFailed += (_, f) => failure = f;
        _sut.LayerTransformTool.KeepAspect = false;
        _sut.LayerTransformTool.SetSize(20000, 10);

        _sut.ApplyLayerTransform().Should().BeFalse();

        failure.Should().Be(LayerTransformFailure.TooLarge);
        layer.Bounds.Should().Be(new SKRectI(0, 0, 100, 100));
        _sut.LayerTransformTool.HasTransform.Should().BeTrue(); // kept so the user can shrink it
    }

    [Fact]
    public void NothingToApply_ReturnsFalse_WithoutFailureEvent()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var failed = 0;
        _sut.LayerTransformFailed += (_, _) => failed++;
        _sut.ApplyLayerTransform().Should().BeFalse();
        failed.Should().Be(0);
    }

    [Fact]
    public void ChangingActiveLayer_CommitsPending_ThenArmsNewLayer()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var first = _sut.ActiveLayer!;
        _sut.LayerTransformTool.Nudge(10, 0);
        var second = _services.Layers.AddLayer("second")!; // AddLayer makes it active via the stack, not via the core setter

        _sut.ActiveLayer = first;   // no pending change on first anymore? we set it again to exercise the setter
        _sut.ActiveLayer = second;

        first.OffsetX.Should().Be(10);
        _sut.LayerTransformTool.Layer.Should().BeSameAs(second);
        _sut.LayerTransformTool.HasTransform.Should().BeFalse();
    }

    [Fact]
    public void RenderWithZoom_PreviewsTheMatrix_WithoutChangingTheLayer()
    {
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform();
        var layer = _sut.ActiveLayer!;
        _sut.LayerTransformTool.Nudge(50, 0);

        using var surface = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(surface);
        _sut.ZoomToActual();
        _sut.RenderWithZoom(canvas, 100, 100, SKColors.Black);

        layer.Bounds.Should().Be(new SKRectI(0, 0, 100, 100));
        surface.GetPixel(75, 50).Should().Be(SKColors.Red);
        surface.GetPixel(25, 50).Should().NotBe(SKColors.Red); // checkerboard/background, layer moved away
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `ImageEditorCoreLayerTransformTests`. Expected: build errors.

- [ ] **Step 3: Implement**

**3a. `ImageEditorCore.cs`:**

After `public CanvasExtendTool CanvasExtendTool { get; } = new();` add:

```csharp
    /// <summary>Gets the layer Move / Transform tool.</summary>
    public LayerTransformTool LayerTransformTool { get; }
```

and in the constructor (add a parameterless constructor if none exists; there is `public ImageEditorCore()` implicitly — add explicitly):

```csharp
    public ImageEditorCore()
    {
        LayerTransformTool = new LayerTransformTool();
        LayerTransformTool.CommitRequested += (_, _) => ApplyLayerTransform();
    }
```

`ActiveLayer` setter:

```csharp
        set
        {
            if (_services is null) return;
            if (ReferenceEquals(_services.Layers.ActiveLayer, value)) return;
            // Switching layers with a pending transform commits it first (Shape/Text precedent).
            if (LayerTransformTool.IsActive && LayerTransformTool.IsArmed)
                LayerTransformTool.Commit();
            _services.Layers.ActiveLayer = value;
            if (LayerTransformTool.IsActive)
                ArmLayerTransform();
        }
```

`RenderWithZoom`: replace `LayerCompositor.CompositeToCanvas(canvas, _layers, imageRect);` with

```csharp
                var preview = LayerTransformTool.IsActive && LayerTransformTool.IsArmed && LayerTransformTool.HasTransform
                    && LayerTransformTool.Layer is { } previewLayer
                    ? new LayerRenderOverride(previewLayer, LayerTransformTool.Matrix)
                    : (LayerRenderOverride?)null;
                LayerCompositor.CompositeToCanvas(canvas, _layers, imageRect, preview);
```

and after the `CanvasExtendTool.Render(...)` block add:

```csharp
            // Update the layer transform tool with current image bounds and render overlay
            LayerTransformTool.SetImageBounds(imageRect);
            LayerTransformTool.ImagePixelWidth = imageWidth;
            LayerTransformTool.ImagePixelHeight = imageHeight;
            LayerTransformTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));
```

**3b. New `ImageEditorCore.LayerTransform.cs`:**

```csharp
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>Why the active layer can or cannot be transformed.</summary>
public enum LayerTransformEligibility { Ok, NoLayer, InpaintMask, Locked }

/// <summary>Why <see cref="ImageEditorCore.ApplyLayerTransform"/> refused.</summary>
public enum LayerTransformFailure { TooLarge, Allocation }

public partial class ImageEditorCore
{
    /// <summary>Longest side a committed transform may produce.</summary>
    public const int MaxTransformedSide = 16384;
    /// <summary>Largest pixel count a committed transform may produce (256 M).</summary>
    public const long MaxTransformedArea = 268_435_456L;

    /// <summary>Raised after a transform was rasterized into the layer.</summary>
    public event EventHandler? LayerTransformApplied;

    /// <summary>Raised when a transform could not be applied; the layer is untouched.</summary>
    public event EventHandler<LayerTransformFailure>? LayerTransformFailed;

    /// <summary>Raised by <see cref="ArmLayerTransform"/> with the result, so the panel can show a hint.</summary>
    public event EventHandler<LayerTransformEligibility>? LayerTransformEligibilityChanged;

    /// <summary>
    /// Enables layer mode if needed and arms <see cref="LayerTransformTool"/> with the active
    /// layer, or disarms it when that layer is the inpaint mask, locked, or missing.
    /// </summary>
    public LayerTransformEligibility ArmLayerTransform()
    {
        if (!_isLayerMode && HasImage)
            EnableLayerMode();

        var layer = ActiveLayer;
        var result = layer is null ? LayerTransformEligibility.NoLayer
            : layer.IsInpaintMask ? LayerTransformEligibility.InpaintMask
            : layer.IsLocked ? LayerTransformEligibility.Locked
            : LayerTransformEligibility.Ok;

        if (result == LayerTransformEligibility.Ok)
            LayerTransformTool.Arm(layer!);
        else
            LayerTransformTool.Disarm();

        FileLogger.Log($"Layer transform armed: {result} ({layer?.Name})");
        LayerTransformEligibilityChanged?.Invoke(this, result);
        return result;
    }

    /// <summary>
    /// Rasterizes the tool's transform into the armed layer, all-or-nothing: a new bitmap sized to
    /// the transformed bounds replaces the layer's under the render lock; pure integer moves copy
    /// pixels exactly. Returns false with no event when there is nothing to apply.
    /// </summary>
    public bool ApplyLayerTransform()
    {
        var tool = LayerTransformTool;
        var layer = tool.Layer;
        if (layer?.Bitmap is null || !tool.HasTransform) return false;

        var matrix = tool.Matrix;
        var boundsF = matrix.MapRect(SKRect.Create(layer.Bounds.Left, layer.Bounds.Top, layer.Bounds.Width, layer.Bounds.Height));
        var bounds = SKRectI.Ceiling(boundsF, outwards: true);
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;

        if (bounds.Width > MaxTransformedSide || bounds.Height > MaxTransformedSide ||
            (long)bounds.Width * bounds.Height > MaxTransformedArea)
        {
            FileLogger.Log($"Layer transform refused: {bounds.Width}x{bounds.Height} exceeds the size guard");
            LayerTransformFailed?.Invoke(this, LayerTransformFailure.TooLarge);
            return false;
        }

        SKBitmap? replaced = null;
        try
        {
            SKBitmap result;
            SKPointI offset;
            if (IsIntegerTranslation(tool, out var dx, out var dy))
            {
                result = layer.Bitmap.Copy() ?? throw new InvalidOperationException("Could not copy the layer.");
                offset = new SKPointI(layer.OffsetX + dx, layer.OffsetY + dy);
            }
            else
            {
                result = new SKBitmap(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                if (result.IsEmpty || result.Width != bounds.Width || result.Height != bounds.Height)
                {
                    result.Dispose();
                    throw new InvalidOperationException($"Could not allocate a {bounds.Width}x{bounds.Height} layer.");
                }
                result.Erase(SKColors.Transparent);
                using var canvas = new SKCanvas(result);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.Concat(matrix);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawBitmap(layer.Bitmap, layer.OffsetX, layer.OffsetY, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
                offset = new SKPointI(bounds.Left, bounds.Top);
            }

            lock (_bitmapLock)
            {
                replaced = layer.Bitmap;
                layer.AdoptBitmapKeepingOld(result, offset);
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogError($"Layer transform {bounds.Width}x{bounds.Height} failed", ex);
            LayerTransformFailed?.Invoke(this, LayerTransformFailure.Allocation);
            return false;
        }

        replaced?.Dispose();
        FileLogger.Log($"Layer transform applied: '{layer.Name}' -> {bounds}");
        tool.Arm(layer); // identity again on the same layer
        OnImageChanged();
        LayerTransformApplied?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static bool IsIntegerTranslation(LayerTransformTool tool, out int dx, out int dy)
    {
        dx = (int)MathF.Round(tool.Translation.X);
        dy = (int)MathF.Round(tool.Translation.Y);
        return MathF.Abs(tool.RotationDegrees) < 1e-3f
            && MathF.Abs(tool.ScaleX - 1f) < 1e-3f && MathF.Abs(tool.ScaleY - 1f) < 1e-3f
            && MathF.Abs(tool.Translation.X - dx) < 1e-3f && MathF.Abs(tool.Translation.Y - dy) < 1e-3f;
    }
}
```

`Layer.AdoptBitmap` disposes the old bitmap inside the call; the render-lock rule wants disposal **after** the lock. Add to `Layer.cs`:

```csharp
    /// <summary>
    /// Like <see cref="AdoptBitmap(SKBitmap, SKPointI)"/> but hands the previous bitmap back
    /// instead of disposing it, for callers that must dispose outside the render lock.
    /// </summary>
    internal SKBitmap? AdoptBitmapKeepingOld(SKBitmap newBitmap, SKPointI offset)
    {
        var old = _bitmap;
        _bitmap = newBitmap;
        _offsetX = offset.X;
        _offsetY = offset.Y;
        UpdateThumbnail();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        return old;
    }
```

and in `ApplyLayerTransform` use `replaced = layer.AdoptBitmapKeepingOld(result, offset);` (drop the separate `replaced = layer.Bitmap;` line).

Check `SKRectI.Ceiling(SKRect, bool outwards)` exists in SkiaSharp 3.119 (`SKRectI.Ceiling(SKRect value, bool outwards)`); if not, use `new SKRectI((int)MathF.Floor(boundsF.Left), (int)MathF.Floor(boundsF.Top), (int)MathF.Ceiling(boundsF.Right), (int)MathF.Ceiling(boundsF.Bottom))`.

- [ ] **Step 4: Run tests** — filters `ImageEditorCoreLayerTransformTests`, `ImageEditorCoreRenderRaceTests`, `ImageEditorCoreCanvasExtendTests`. Expected: green. In `ChangingActiveLayer_CommitsPending_ThenArmsNewLayer`, `AddLayer` changes the stack's active layer directly; the test's `_sut.ActiveLayer = first` re-arms `first` at identity — so move the `Nudge(10, 0)` to **after** `_sut.ActiveLayer = first;` if the assertion on `first.OffsetX` fails. Fix the test, not the setter.

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/ImageEditor DiffusionNexus.Tests/ImageEditor/ImageEditorCoreLayerTransformTests.cs
git commit -m "feat(editor): arm, preview and rasterize a layer transform in the core

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 8: `LayerTransformViewModel` and `ToolIds.LayerTransform`

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/Services/ToolIds.cs`
- Create: `DiffusionNexus.UI/ViewModels/LayerTransformViewModel.cs`
- Test: `DiffusionNexus.Tests/ViewModels/LayerTransformViewModelTests.cs` (create)

**Interfaces:**
- Consumes: `LayerTransformEligibility`, `LayerTransformFailure` (Task 7), `ToolIds`.
- Produces (bound by Task 11 XAML, wired by Task 11 code-behind): properties `IsPanelOpen`, `LayerName`, `X`, `Y`, `Width`, `Height` (int), `RotationDegrees` (float), `KeepAspect`, `HasTransform`, `HintText`, `IsHintVisible`; commands `ToggleCommand`, `CancelCommand`, `ResetCommand`, `ApplyCommand`, `FlipHorizontalCommand`, `FlipVerticalCommand`; events `ToolActivated`, `ToolDeactivated`, `ToolToggled`, `ToolStateChanged`, `StatusMessageChanged`, `PositionRequested (float X, float Y)`, `SizeRequested (float W, float H)`, `RotationRequested float`, `KeepAspectChanged bool`, `FlipRequested bool horizontal`, `ResetRequested`, `ApplyRequested`; methods `UpdateFromTool(string layerName, float x, float y, float w, float h, float rotation, bool hasTransform)`, `OnIneligible(LayerTransformEligibility)`, `OnApplied()`, `OnApplyFailed(LayerTransformFailure)`, `RefreshCommandStates()`, `ClosePanel()`. Constants `IneligibleHintText`, `TooLargeHintText`, `AllocationHintText`, `StatusOnOpen`.

- [ ] **Step 1: Write the failing tests**

```csharp
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Panel state for the Move / Transform tool: mutual exclusion on open, fields synced from the
/// tool without echoing a request back, typed values forwarded as requests, Apply gated on a
/// transform, and the amber hint for ineligible layers and refused commits.
/// </summary>
public class LayerTransformViewModelTests
{
    private readonly List<string> _deactivated = [];
    private readonly LayerTransformViewModel _sut;

    public LayerTransformViewModelTests()
    {
        _sut = new LayerTransformViewModel(() => true, id => _deactivated.Add(id));
    }

    [Fact]
    public void Opening_DeactivatesOtherTools_RaisesToggle_AndStatus()
    {
        (string ToolId, bool IsActive)? toggled = null;
        string? status = null;
        var activated = 0;
        _sut.ToolToggled += (_, a) => toggled = a;
        _sut.StatusMessageChanged += (_, s) => status = s;
        _sut.ToolActivated += (_, _) => activated++;

        _sut.IsPanelOpen = true;

        _deactivated.Should().ContainSingle().Which.Should().Be(ToolIds.LayerTransform);
        toggled.Should().Be((ToolIds.LayerTransform, true));
        status.Should().Be(LayerTransformViewModel.StatusOnOpen);
        activated.Should().Be(1);
    }

    [Fact]
    public void UpdateFromTool_SetsFields_WithoutRaisingRequests()
    {
        _sut.IsPanelOpen = true;
        var requests = 0;
        _sut.PositionRequested += (_, _) => requests++;
        _sut.SizeRequested += (_, _) => requests++;
        _sut.RotationRequested += (_, _) => requests++;

        _sut.UpdateFromTool("Background", 10.4f, 20.6f, 300f, 200f, 12.34f, hasTransform: true);

        _sut.LayerName.Should().Be("Background");
        _sut.X.Should().Be(10);
        _sut.Y.Should().Be(21);
        _sut.Width.Should().Be(300);
        _sut.Height.Should().Be(200);
        _sut.RotationDegrees.Should().BeApproximately(12.3f, 0.01f);
        _sut.HasTransform.Should().BeTrue();
        requests.Should().Be(0);
    }

    [Fact]
    public void TypedValues_RaiseRequests()
    {
        _sut.IsPanelOpen = true;
        (float X, float Y)? pos = null; (float W, float H)? size = null; float? rot = null; bool? aspect = null;
        _sut.PositionRequested += (_, p) => pos = p;
        _sut.SizeRequested += (_, s) => size = s;
        _sut.RotationRequested += (_, r) => rot = r;
        _sut.KeepAspectChanged += (_, k) => aspect = k;
        _sut.UpdateFromTool("L", 0, 0, 100, 50, 0, false);

        _sut.X = 5;
        _sut.Width = 200;
        _sut.RotationDegrees = 90f;
        _sut.KeepAspect = false;

        pos.Should().Be((5f, 0f));
        size.Should().Be((200f, 50f));
        rot.Should().Be(90f);
        aspect.Should().Be(false);
    }

    [Fact]
    public void Apply_IsGatedOnHasTransform_AndRaisesApplyRequested()
    {
        _sut.IsPanelOpen = true;
        var applied = 0;
        _sut.ApplyRequested += (_, _) => applied++;
        _sut.ApplyCommand.CanExecute(null).Should().BeFalse();

        _sut.UpdateFromTool("L", 0, 0, 1, 1, 0, hasTransform: true);
        _sut.ApplyCommand.CanExecute(null).Should().BeTrue();
        _sut.ApplyCommand.Execute(null);
        applied.Should().Be(1);
    }

    [Fact]
    public void FlipsAndReset_RaiseRequests()
    {
        _sut.IsPanelOpen = true;
        var flips = new List<bool>();
        var resets = 0;
        _sut.FlipRequested += (_, h) => flips.Add(h);
        _sut.ResetRequested += (_, _) => resets++;

        _sut.FlipHorizontalCommand.Execute(null);
        _sut.FlipVerticalCommand.Execute(null);
        _sut.ResetCommand.Execute(null);

        flips.Should().Equal(true, false);
        resets.Should().Be(1);
    }

    [Fact]
    public void Ineligible_ShowsHint_AndClearsOnEligible()
    {
        _sut.IsPanelOpen = true;
        _sut.OnIneligible(LayerTransformEligibility.InpaintMask);
        _sut.IsHintVisible.Should().BeTrue();
        _sut.HintText.Should().Be(LayerTransformViewModel.IneligibleHintText);

        _sut.OnIneligible(LayerTransformEligibility.Ok);
        _sut.IsHintVisible.Should().BeFalse();
    }

    [Fact]
    public void ApplyFailed_ShowsReasonHint_AndApplied_ClearsIt()
    {
        _sut.IsPanelOpen = true;
        _sut.OnApplyFailed(LayerTransformFailure.TooLarge);
        _sut.HintText.Should().Be(LayerTransformViewModel.TooLargeHintText);
        _sut.OnApplyFailed(LayerTransformFailure.Allocation);
        _sut.HintText.Should().Be(LayerTransformViewModel.AllocationHintText);

        _sut.OnApplied();
        _sut.IsHintVisible.Should().BeFalse();
        _sut.IsPanelOpen.Should().BeTrue(); // the tool stays open after Apply
    }

    [Fact]
    public void Cancel_ClosesPanel_AndRaisesDeactivated()
    {
        _sut.IsPanelOpen = true;
        var deactivated = 0;
        _sut.ToolDeactivated += (_, _) => deactivated++;
        _sut.CancelCommand.Execute(null);
        _sut.IsPanelOpen.Should().BeFalse();
        deactivated.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `LayerTransformViewModelTests`. Expected: build errors.

- [ ] **Step 3: Implement**

`ToolIds.cs`: add `public const string LayerTransform = "LayerTransform";`.

`LayerTransformViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Panel state for the Move / Transform tool. Owns nothing pixel-related: it raises requests
/// (position, size, rotation, flips, reset, apply) that the view forwards to
/// <see cref="LayerTransformTool"/>, and it is told the tool's state through
/// <see cref="UpdateFromTool"/> / <see cref="OnIneligible"/> / <see cref="OnApplied"/> /
/// <see cref="OnApplyFailed"/>.
/// </summary>
public partial class LayerTransformViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<LayerTransformViewModel>();
    private const string LogSource = "LayerTransform";

    public const string IneligibleHintText = "This layer can't be moved. Select another layer.";
    public const string TooLargeHintText = "The transformed layer would be too large.";
    public const string AllocationHintText = "Could not allocate the transformed layer.";
    public const string StatusOnOpen = "Move: Drag the layer to move it, use the handles to scale or rotate. Enter applies, Escape resets.";

    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IUnifiedLogger? _unifiedLogger;

    private bool _isPanelOpen;
    private string _layerName = string.Empty;
    private int _x, _y, _width, _height;
    private float _rotationDegrees;
    private bool _keepAspect = true;
    private bool _hasTransform;
    private string _hintText = string.Empty;
    private bool _isHintVisible;
    private bool _syncing;

    public LayerTransformViewModel(Func<bool> hasImage, Action<string> deactivateOtherTools, IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;
        _unifiedLogger = unifiedLogger;

        ToggleCommand = new RelayCommand(() => IsPanelOpen = !IsPanelOpen, () => _hasImage());
        CancelCommand = new RelayCommand(() => IsPanelOpen = false, () => IsPanelOpen);
        ResetCommand = new RelayCommand(() => { EmitInfo("reset requested"); ResetRequested?.Invoke(this, EventArgs.Empty); }, () => IsPanelOpen);
        ApplyCommand = new RelayCommand(() => { EmitInfo("apply requested"); ApplyRequested?.Invoke(this, EventArgs.Empty); }, () => _hasImage() && IsPanelOpen && HasTransform);
        FlipHorizontalCommand = new RelayCommand(() => { EmitInfo("flip horizontal"); FlipRequested?.Invoke(this, true); }, () => IsPanelOpen);
        FlipVerticalCommand = new RelayCommand(() => { EmitInfo("flip vertical"); FlipRequested?.Invoke(this, false); }, () => IsPanelOpen);
    }

    #region Properties

    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (!SetProperty(ref _isPanelOpen, value)) return;
            IsHintVisible = false;
            if (value)
            {
                _deactivateOtherTools(ToolIds.LayerTransform);
                EmitInfo("panel opened");
                ToolActivated?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, StatusOnOpen);
            }
            else
            {
                EmitInfo("panel closed");
                ToolDeactivated?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, null);
            }
            RefreshCommandStates();
            ToolToggled?.Invoke(this, (ToolIds.LayerTransform, value));
            ToolStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string LayerName { get => _layerName; private set => SetProperty(ref _layerName, value); }

    /// <summary>Left of the transformed box, canvas px.</summary>
    public int X
    {
        get => _x;
        set { if (SetProperty(ref _x, value) && !_syncing) PositionRequested?.Invoke(this, (value, _y)); }
    }

    public int Y
    {
        get => _y;
        set { if (SetProperty(ref _y, value) && !_syncing) PositionRequested?.Invoke(this, (_x, value)); }
    }

    public int Width
    {
        get => _width;
        set { if (SetProperty(ref _width, value) && !_syncing && value > 0) SizeRequested?.Invoke(this, (value, _height)); }
    }

    public int Height
    {
        get => _height;
        set { if (SetProperty(ref _height, value) && !_syncing && value > 0) SizeRequested?.Invoke(this, (_width, value)); }
    }

    public float RotationDegrees
    {
        get => _rotationDegrees;
        set { if (SetProperty(ref _rotationDegrees, value) && !_syncing) RotationRequested?.Invoke(this, value); }
    }

    public bool KeepAspect
    {
        get => _keepAspect;
        set { if (SetProperty(ref _keepAspect, value)) KeepAspectChanged?.Invoke(this, value); }
    }

    public bool HasTransform
    {
        get => _hasTransform;
        private set { if (SetProperty(ref _hasTransform, value)) ApplyCommand.NotifyCanExecuteChanged(); }
    }

    public string HintText { get => _hintText; private set => SetProperty(ref _hintText, value); }
    public bool IsHintVisible { get => _isHintVisible; private set => SetProperty(ref _isHintVisible, value); }

    #endregion

    #region Commands

    public IRelayCommand ToggleCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand FlipHorizontalCommand { get; }
    public IRelayCommand FlipVerticalCommand { get; }

    #endregion

    #region Events

    public event EventHandler? ToolActivated;
    public event EventHandler? ToolDeactivated;
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;
    public event EventHandler? ToolStateChanged;
    public event EventHandler<string?>? StatusMessageChanged;
    public event EventHandler<(float X, float Y)>? PositionRequested;
    public event EventHandler<(float W, float H)>? SizeRequested;
    public event EventHandler<float>? RotationRequested;
    public event EventHandler<bool>? KeepAspectChanged;
    /// <summary>true = horizontal, false = vertical.</summary>
    public event EventHandler<bool>? FlipRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? ApplyRequested;

    #endregion

    #region Public methods

    public void RefreshCommandStates()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        FlipHorizontalCommand.NotifyCanExecuteChanged();
        FlipVerticalCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Closes the panel without deactivating other tools (tool coordination).</summary>
    public void ClosePanel()
    {
        if (!_isPanelOpen) return;
        _isPanelOpen = false;
        OnPropertyChanged(nameof(IsPanelOpen));
        IsHintVisible = false;
        ToolDeactivated?.Invoke(this, EventArgs.Empty);
        RefreshCommandStates();
    }

    /// <summary>Called by the view on every tool change. Syncs fields without echoing requests.</summary>
    public void UpdateFromTool(string layerName, float x, float y, float w, float h, float rotation, bool hasTransform)
    {
        _syncing = true;
        try
        {
            LayerName = layerName;
            X = (int)MathF.Round(x);
            Y = (int)MathF.Round(y);
            Width = (int)MathF.Round(w);
            Height = (int)MathF.Round(h);
            RotationDegrees = MathF.Round(rotation, 1);
            HasTransform = hasTransform;
        }
        finally
        {
            _syncing = false;
        }
    }

    public void OnIneligible(LayerTransformEligibility eligibility)
    {
        if (eligibility == LayerTransformEligibility.Ok) { IsHintVisible = false; return; }
        EmitInfo($"active layer not eligible: {eligibility}");
        HintText = IneligibleHintText;
        IsHintVisible = true;
        HasTransform = false;
    }

    public void OnApplied()
    {
        EmitInfo("applied");
        IsHintVisible = false;
        StatusMessageChanged?.Invoke(this, "Layer transformed.");
    }

    public void OnApplyFailed(LayerTransformFailure failure)
    {
        EmitInfo($"apply failed: {failure}");
        HintText = failure == LayerTransformFailure.TooLarge ? TooLargeHintText : AllocationHintText;
        IsHintVisible = true;
    }

    #endregion

    private void EmitInfo(string message)
    {
        Logger.Information("LayerTransform: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Configuration, LogSource, message); // same category CanvasExtendViewModel uses
    }
}
```

(Check `CanvasExtendViewModel.EmitInfo` for the exact `IUnifiedLogger.Info` signature and copy it.)

- [ ] **Step 4: Run tests** — filter `LayerTransformViewModelTests`. Expected: 8 passed.

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/ImageEditor/Services/ToolIds.cs DiffusionNexus.UI/ViewModels/LayerTransformViewModel.cs DiffusionNexus.Tests/ViewModels/LayerTransformViewModelTests.cs
git commit -m "feat(editor): LayerTransformViewModel panel state and ToolIds.LayerTransform

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 9: Register the sub-view-model in `ImageEditorViewModel`

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs`
- Test: `DiffusionNexus.Tests/ViewModels/ImageEditorViewModelLayerTransformTests.cs` (create)

**Interfaces:**
- Consumes: Task 8 `LayerTransformViewModel`.
- Produces: `public LayerTransformViewModel LayerTransform { get; }` on `ImageEditorViewModel`.

- [ ] **Step 1: Write the failing test**

Look at how existing `ImageEditorViewModel` tests construct the view model (`DiffusionNexus.Tests/ViewModels/ImageEditorViewModelExportExtensionTests.cs` — copy its constructor helper verbatim into this class as `CreateViewModel()`).

```csharp
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>Opening the Move panel is mutually exclusive with the other tools, like every panel.</summary>
public class ImageEditorViewModelLayerTransformTests
{
    [Fact]
    public void OpeningMove_ClosesCrop_AndActivatesTheToolId()
    {
        var vm = CreateViewModel(); // copied helper; must load or fake HasImage = true the way the sibling tests do
        vm.IsCropToolActive = true;

        vm.LayerTransform.IsPanelOpen = true;

        vm.IsCropToolActive.Should().BeFalse();
        vm.Services.Tools.ActiveToolId.Should().Be(ToolIds.LayerTransform);
    }

    [Fact]
    public void OpeningCrop_ClosesMove()
    {
        var vm = CreateViewModel();
        vm.LayerTransform.IsPanelOpen = true;

        vm.IsCropToolActive = true;

        vm.LayerTransform.IsPanelOpen.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `ImageEditorViewModelLayerTransformTests`. Expected: build error, `LayerTransform` missing.

- [ ] **Step 3: Implement**

In `ImageEditorViewModel.cs`:

- Property, after `CanvasExtend`: `/// <summary>Sub-ViewModel for the Move / Transform tool.</summary> public LayerTransformViewModel LayerTransform { get; }`
- Constructor, after the `CanvasExtend = new ...` line: `LayerTransform = new LayerTransformViewModel(() => HasImage, DeactivateOtherTools, unifiedLogger);`
- In `WireSubViewModelEvents`, after the `CanvasExtend.OpenCropRequested` block:

```csharp
        LayerTransform.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        LayerTransform.StatusMessageChanged += (_, msg) => StatusMessage = msg;
        LayerTransform.ToolToggled += (_, args) =>
        {
            if (args.IsActive) _services.Tools.Activate(args.ToolId);
            else _services.Tools.Deactivate(args.ToolId);
        };
```

- `DeactivateOtherTools`: append `if (exceptToolId != ToolIds.LayerTransform) LayerTransform.ClosePanel();`
- `CloseAllTools`: append `LayerTransform.ClosePanel();`
- `NotifyToolCommandsCanExecuteChanged`: append `LayerTransform.RefreshCommandStates();`

- [ ] **Step 4: Run tests** — filter `ImageEditorViewModel`. Expected: green.

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs DiffusionNexus.Tests/ViewModels/ImageEditorViewModelLayerTransformTests.cs
git commit -m "feat(editor): register the Move / Transform panel in the editor view model

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 10: `ImageEditorControl` — activation, pointer routing, keys, cursor, events

**Files:**
- Modify: `DiffusionNexus.UI/Controls/ImageEditorControl.cs`

No headless unit test drives Avalonia input here (the existing tools have none either); the build plus Task 12's GUI smoke covers it. Keep every addition a thin forward to the tool so the tested tool carries the logic.

**Interfaces:**
- Consumes: Task 6 tool, Task 7 core members.
- Produces: `bool IsLayerTransformToolActive`, `bool ApplyLayerTransform()`, events `LayerTransformChanged`, `LayerTransformApplied`, `LayerTransformFailed (EventHandler<LayerTransformFailure>)`, `LayerTransformEligibilityChanged (EventHandler<LayerTransformEligibility>)`.

- [ ] **Step 1: Field + property** — after `_isCanvasExtendToolActive` add `private bool _isLayerTransformToolActive;`; after `IsCanvasExtendToolActive` add:

```csharp
    /// <summary>Gets or sets whether the layer Move / Transform tool is active.</summary>
    public bool IsLayerTransformToolActive
    {
        get => _isLayerTransformToolActive;
        set
        {
            _isLayerTransformToolActive = value;
            _editorCore.LayerTransformTool.IsActive = value;   // false commits a pending transform
            if (value) _editorCore.ArmLayerTransform();
            InvalidateVisual();
        }
    }
```

- [ ] **Step 2: Pointer routing** — in `OnPointerPressed`, after the Canvas Extend block and before `CropTool.OnPointerPressed`:

```csharp
        // Layer transform tool takes priority when active
        if (_isLayerTransformToolActive && props.IsLeftButtonPressed)
        {
            var tool = _editorCore.LayerTransformTool;
            tool.ConstrainProportionsOverride = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            tool.SnapRotation = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (tool.OnPointerPressed(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                Focus();
                return;
            }
        }
```

In `OnPointerMoved`, after the Canvas Extend block:

```csharp
        if (_isLayerTransformToolActive)
        {
            var tool = _editorCore.LayerTransformTool;
            tool.ConstrainProportionsOverride = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            tool.SnapRotation = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (tool.OnPointerMoved(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }
```

In `OnPointerReleased`, after the Canvas Extend block:

```csharp
        if (_isLayerTransformToolActive)
        {
            if (_editorCore.LayerTransformTool.OnPointerReleased())
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }
```

- [ ] **Step 3: Keys** — in `OnKeyDown`, before the Canvas Extend block:

```csharp
        // Layer transform: Enter applies, Escape resets (tool stays open), arrows nudge (Shift = 10 px)
        if (_isLayerTransformToolActive && _editorCore.LayerTransformTool.IsArmed)
        {
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            switch (e.Key)
            {
                case Key.Enter: ApplyLayerTransform(); e.Handled = true; return;
                case Key.Escape: _editorCore.LayerTransformTool.Reset(); InvalidateVisual(); e.Handled = true; return;
                case Key.Left: _editorCore.LayerTransformTool.Nudge(-step, 0); InvalidateVisual(); e.Handled = true; return;
                case Key.Right: _editorCore.LayerTransformTool.Nudge(step, 0); InvalidateVisual(); e.Handled = true; return;
                case Key.Up: _editorCore.LayerTransformTool.Nudge(0, -step); InvalidateVisual(); e.Handled = true; return;
                case Key.Down: _editorCore.LayerTransformTool.Nudge(0, step); InvalidateVisual(); e.Handled = true; return;
            }
        }
```

- [ ] **Step 4: Cursor** — in `UpdateCursor`, before the `if (!_editorCore.CropTool.IsActive)` fallback:

```csharp
        if (_isLayerTransformToolActive)
        {
            Cursor = _editorCore.LayerTransformTool.GetCursorForPoint(point) switch
            {
                ImageEditor.TransformHandle.None => Cursor.Default,
                ImageEditor.TransformHandle.Rotate => new Cursor(StandardCursorType.Hand),
                ImageEditor.TransformHandle.Top or ImageEditor.TransformHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
                ImageEditor.TransformHandle.Left or ImageEditor.TransformHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
                _ => new Cursor(StandardCursorType.SizeAll)
            };
            return;
        }
```

- [ ] **Step 5: Events + apply wrapper** — next to `ApplyCanvasExtend`:

```csharp
    /// <summary>Raised whenever the layer transform tool's state changes (fields sync).</summary>
    public event EventHandler? LayerTransformChanged;
    /// <summary>Raised after a layer transform was rasterized.</summary>
    public event EventHandler? LayerTransformApplied;
    /// <summary>Raised when a layer transform was refused; the layer is untouched.</summary>
    public event EventHandler<ImageEditor.LayerTransformFailure>? LayerTransformFailed;
    /// <summary>Raised when the tool (re)armed, with the active layer's eligibility.</summary>
    public event EventHandler<ImageEditor.LayerTransformEligibility>? LayerTransformEligibilityChanged;

    /// <summary>Applies the pending layer transform. False with no event when there is nothing to apply.</summary>
    public bool ApplyLayerTransform()
    {
        var result = _editorCore.ApplyLayerTransform();
        InvalidateVisual();
        return result;
    }

    private void OnLayerTransformChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        LayerTransformChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLayerTransformApplied(object? sender, EventArgs e) => LayerTransformApplied?.Invoke(this, EventArgs.Empty);
    private void OnLayerTransformFailed(object? sender, ImageEditor.LayerTransformFailure f) => LayerTransformFailed?.Invoke(this, f);
    private void OnLayerTransformEligibilityChanged(object? sender, ImageEditor.LayerTransformEligibility r) => LayerTransformEligibilityChanged?.Invoke(this, r);
```

In `OnAttachedToVisualTree` add:

```csharp
        _editorCore.LayerTransformTool.TransformChanged += OnLayerTransformChanged;
        _editorCore.LayerTransformTool.ArmedLayerChanged += OnLayerTransformChanged;
        _editorCore.LayerTransformApplied += OnLayerTransformApplied;
        _editorCore.LayerTransformFailed += OnLayerTransformFailed;
        _editorCore.LayerTransformEligibilityChanged += OnLayerTransformEligibilityChanged;
```

and the matching `-=` lines in `OnDetachedFromVisualTree`.

- [ ] **Step 6: Build** — `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj --nologo -v q`. Expected: 0 errors.

- [ ] **Step 7: Commit + push**

```bash
git add DiffusionNexus.UI/Controls/ImageEditorControl.cs
git commit -m "feat(editor): route pointer, keys and cursor to the layer transform tool

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 11: Toolbar toggle, panel XAML and view wiring

**Files:**
- Modify: `DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml` (toolbar after the Crop toggle; panel after the Crop Tool Panel `</StackPanel>`)
- Modify: `DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml.cs` (`WireLayerTransformEvents`, called after `WireCanvasExtendEvents(imageEditor);`)

**Interfaces:**
- Consumes: Task 8 view model, Task 10 control members, Task 6 tool setters.

- [ ] **Step 1: Toolbar toggle** — directly after the `Crop` `ToggleButton`:

```xml
            <ToggleButton Content="Move" IsChecked="{Binding ImageEditor.LayerTransform.IsPanelOpen, Mode=TwoWay}" Padding="12,6"
                          ToolTip.Tip="Move / Transform - Move, scale, rotate or flip the selected layer"
                          IsEnabled="{Binding ImageEditor.HasImage}"/>
```

- [ ] **Step 2: Panel** — after the Crop Tool Panel's closing `</StackPanel>` (the one whose `IsVisible` binds `ImageEditor.IsCropToolActive`):

```xml
          <!-- Move / Transform Panel -->
          <StackPanel Spacing="8" IsVisible="{Binding ImageEditor.LayerTransform.IsPanelOpen, FallbackValue=False}">
            <TextBlock Text="Move / Transform" FontWeight="SemiBold" FontSize="14"/>
            <Border Background="#2A2A2A" CornerRadius="4" Padding="8">
              <StackPanel Spacing="8">
                <TextBlock Text="Drag the layer to move it. Handles scale, the top handle rotates."
                           FontSize="11" Opacity="0.7" TextWrapping="Wrap"/>
                <TextBlock Text="{Binding ImageEditor.LayerTransform.LayerName, StringFormat='Layer: {0}'}" FontSize="12" FontWeight="SemiBold"
                           Foreground="#4CAF50" HorizontalAlignment="Center"
                           IsVisible="{Binding ImageEditor.LayerTransform.LayerName, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>

                <Border Height="1" Background="#444" Margin="0,4"/>

                <!-- Position -->
                <TextBlock Text="Position" FontSize="11" Opacity="0.7"/>
                <Grid ColumnDefinitions="Auto,*,8,Auto,*">
                  <TextBlock Grid.Column="0" Text="X" FontSize="11" Opacity="0.7" VerticalAlignment="Center" Margin="0,0,6,0"/>
                  <NumericUpDown Grid.Column="1" Value="{Binding ImageEditor.LayerTransform.X, Mode=TwoWay}"
                                 Minimum="-32768" Maximum="32768" Increment="1"
                                 FormatString="0" FontSize="12" MinWidth="0" ShowButtonSpinner="False"
                                 ToolTip.Tip="Left edge of the layer in canvas pixels"/>
                  <TextBlock Grid.Column="3" Text="Y" FontSize="11" Opacity="0.7" VerticalAlignment="Center" Margin="0,0,6,0"/>
                  <NumericUpDown Grid.Column="4" Value="{Binding ImageEditor.LayerTransform.Y, Mode=TwoWay}"
                                 Minimum="-32768" Maximum="32768" Increment="1"
                                 FormatString="0" FontSize="12" MinWidth="0" ShowButtonSpinner="False"
                                 ToolTip.Tip="Top edge of the layer in canvas pixels"/>
                </Grid>

                <!-- Size -->
                <TextBlock Text="Size" FontSize="11" Opacity="0.7"/>
                <Grid ColumnDefinitions="Auto,*,8,Auto,*">
                  <TextBlock Grid.Column="0" Text="W" FontSize="11" Opacity="0.7" VerticalAlignment="Center" Margin="0,0,6,0"/>
                  <NumericUpDown Grid.Column="1" Value="{Binding ImageEditor.LayerTransform.Width, Mode=TwoWay}"
                                 Minimum="1" Maximum="16384" Increment="1"
                                 FormatString="0" FontSize="12" MinWidth="0" ShowButtonSpinner="False"
                                 ToolTip.Tip="Layer width in pixels"/>
                  <TextBlock Grid.Column="3" Text="H" FontSize="11" Opacity="0.7" VerticalAlignment="Center" Margin="0,0,6,0"/>
                  <NumericUpDown Grid.Column="4" Value="{Binding ImageEditor.LayerTransform.Height, Mode=TwoWay}"
                                 Minimum="1" Maximum="16384" Increment="1"
                                 FormatString="0" FontSize="12" MinWidth="0" ShowButtonSpinner="False"
                                 ToolTip.Tip="Layer height in pixels"/>
                </Grid>
                <CheckBox Content="Keep aspect" IsChecked="{Binding ImageEditor.LayerTransform.KeepAspect, Mode=TwoWay}" FontSize="11"
                          ToolTip.Tip="Corner handles and the width field keep the layer's proportions. Hold Ctrl while dragging a corner to invert."/>

                <!-- Rotation -->
                <Grid ColumnDefinitions="Auto,*,Auto">
                  <TextBlock Grid.Column="0" Text="Rotation" FontSize="11" Opacity="0.7" VerticalAlignment="Center" Margin="0,0,6,0"/>
                  <NumericUpDown Grid.Column="1" Value="{Binding ImageEditor.LayerTransform.RotationDegrees, Mode=TwoWay}"
                                 Minimum="-180" Maximum="180" Increment="1"
                                 FormatString="0.0" FontSize="12" MinWidth="0" ShowButtonSpinner="False"
                                 ToolTip.Tip="Rotation in degrees. Hold Shift while dragging the top handle to snap to 15°"/>
                  <TextBlock Grid.Column="2" Text="°" FontSize="11" Opacity="0.7" VerticalAlignment="Center" Margin="4,0,0,0"/>
                </Grid>

                <StackPanel Orientation="Horizontal" Spacing="4" HorizontalAlignment="Center">
                  <Button Content="Flip H" Command="{Binding ImageEditor.LayerTransform.FlipHorizontalCommand}" Padding="8,4" FontSize="10" ToolTip.Tip="Mirror the layer left-right"/>
                  <Button Content="Flip V" Command="{Binding ImageEditor.LayerTransform.FlipVerticalCommand}" Padding="8,4" FontSize="10" ToolTip.Tip="Mirror the layer top-bottom"/>
                </StackPanel>

                <Border Height="1" Background="#444" Margin="0,4"/>

                <!-- Ineligible layer / refused commit -->
                <Border Background="#2A2210" CornerRadius="6" Padding="10,8"
                        IsVisible="{Binding ImageEditor.LayerTransform.IsHintVisible}">
                  <StackPanel Orientation="Horizontal" Spacing="8">
                    <Ellipse Width="10" Height="10" Fill="#FFC107" VerticalAlignment="Top" Margin="0,3,0,0"/>
                    <TextBlock Text="{Binding ImageEditor.LayerTransform.HintText}"
                               FontSize="11" Foreground="#FFD54F" TextWrapping="Wrap" MaxWidth="220"/>
                  </StackPanel>
                </Border>

                <!-- Action Buttons -->
                <Grid ColumnDefinitions="*,*,*" Margin="0,4,0,0">
                  <Button Grid.Column="0" Content="Cancel" Command="{Binding ImageEditor.LayerTransform.CancelCommand}" HorizontalAlignment="Stretch" Margin="0,0,2,0"/>
                  <Button Grid.Column="1" Content="Reset" Command="{Binding ImageEditor.LayerTransform.ResetCommand}" HorizontalAlignment="Stretch" Margin="2,0,2,0"/>
                  <Button Grid.Column="2" Content="Apply" Command="{Binding ImageEditor.LayerTransform.ApplyCommand}" Background="#2D7D46" HorizontalAlignment="Stretch" Margin="2,0,0,0"/>
                </Grid>
              </StackPanel>
            </Border>
          </StackPanel>
```

`NumericUpDown.Value` is `decimal?`; Avalonia converts to `int`/`float` targets through the binding — the Extend panel binds an `int` the same way, so `RotationDegrees` (`float`) binds too.

- [ ] **Step 3: Code-behind** — add the call `WireLayerTransformEvents(imageEditor);` after `WireCanvasExtendEvents(imageEditor);` and the method:

```csharp
    private void WireLayerTransformEvents(ImageEditorViewModel imageEditor)
    {
        void PushToolState()
        {
            var tool = _imageEditorCanvas!.EditorCore.LayerTransformTool;
            if (!tool.IsArmed) return;
            var b = tool.TransformedBounds;
            imageEditor.LayerTransform.UpdateFromTool(tool.Layer?.Name ?? string.Empty, b.Left, b.Top, b.Width, b.Height, tool.RotationDegrees, tool.HasTransform);
        }

        EventHandler onActivated = (_, _) =>
        {
            _imageEditorCanvas!.IsLayerTransformToolActive = true;   // arms and raises EligibilityChanged
            _imageEditorCanvas.EditorCore.LayerTransformTool.KeepAspect = imageEditor.LayerTransform.KeepAspect;
            PushToolState();
        };
        imageEditor.LayerTransform.ToolActivated += onActivated;
        _eventCleanup.Add(() => imageEditor.LayerTransform.ToolActivated -= onActivated);

        EventHandler onDeactivated = (_, _) =>
        {
            // Clearing IsLayerTransformToolActive commits a pending transform through IsActive.
            _imageEditorCanvas!.IsLayerTransformToolActive = false;
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerTransform.ToolDeactivated += onDeactivated;
        _eventCleanup.Add(() => imageEditor.LayerTransform.ToolDeactivated -= onDeactivated);

        EventHandler<(float X, float Y)> onPosition = (_, p) => _imageEditorCanvas!.EditorCore.LayerTransformTool.SetPosition(p.X, p.Y);
        imageEditor.LayerTransform.PositionRequested += onPosition;
        _eventCleanup.Add(() => imageEditor.LayerTransform.PositionRequested -= onPosition);

        EventHandler<(float W, float H)> onSize = (_, s) => _imageEditorCanvas!.EditorCore.LayerTransformTool.SetSize(s.W, s.H);
        imageEditor.LayerTransform.SizeRequested += onSize;
        _eventCleanup.Add(() => imageEditor.LayerTransform.SizeRequested -= onSize);

        EventHandler<float> onRotation = (_, r) => _imageEditorCanvas!.EditorCore.LayerTransformTool.SetRotation(r);
        imageEditor.LayerTransform.RotationRequested += onRotation;
        _eventCleanup.Add(() => imageEditor.LayerTransform.RotationRequested -= onRotation);

        EventHandler<bool> onKeepAspect = (_, k) => _imageEditorCanvas!.EditorCore.LayerTransformTool.KeepAspect = k;
        imageEditor.LayerTransform.KeepAspectChanged += onKeepAspect;
        _eventCleanup.Add(() => imageEditor.LayerTransform.KeepAspectChanged -= onKeepAspect);

        EventHandler<bool> onFlip = (_, horizontal) =>
        {
            var tool = _imageEditorCanvas!.EditorCore.LayerTransformTool;
            if (horizontal) tool.FlipHorizontal(); else tool.FlipVertical();
        };
        imageEditor.LayerTransform.FlipRequested += onFlip;
        _eventCleanup.Add(() => imageEditor.LayerTransform.FlipRequested -= onFlip);

        EventHandler onReset = (_, _) => _imageEditorCanvas!.EditorCore.LayerTransformTool.Reset();
        imageEditor.LayerTransform.ResetRequested += onReset;
        _eventCleanup.Add(() => imageEditor.LayerTransform.ResetRequested -= onReset);

        EventHandler onApply = (_, _) => _imageEditorCanvas!.ApplyLayerTransform();
        imageEditor.LayerTransform.ApplyRequested += onApply;
        _eventCleanup.Add(() => imageEditor.LayerTransform.ApplyRequested -= onApply);

        EventHandler onChanged = (_, _) => PushToolState();
        _imageEditorCanvas!.LayerTransformChanged += onChanged;
        _eventCleanup.Add(() => _imageEditorCanvas!.LayerTransformChanged -= onChanged);

        EventHandler<LayerTransformEligibility> onEligibility = (_, r) => imageEditor.LayerTransform.OnIneligible(r);
        _imageEditorCanvas.LayerTransformEligibilityChanged += onEligibility;
        _eventCleanup.Add(() => _imageEditorCanvas!.LayerTransformEligibilityChanged -= onEligibility);

        EventHandler onApplied = (_, _) =>
        {
            imageEditor.LayerTransform.OnApplied();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas!.EditorCore.Layers);
            PushToolState();
        };
        _imageEditorCanvas.LayerTransformApplied += onApplied;
        _eventCleanup.Add(() => _imageEditorCanvas!.LayerTransformApplied -= onApplied);

        EventHandler<LayerTransformFailure> onFailed = (_, f) => imageEditor.LayerTransform.OnApplyFailed(f);
        _imageEditorCanvas.LayerTransformFailed += onFailed;
        _eventCleanup.Add(() => _imageEditorCanvas!.LayerTransformFailed -= onFailed);
    }
```

Add `using DiffusionNexus.UI.ImageEditor;` at the top of the code-behind if `LayerTransformEligibility` does not resolve (the file already references `Layer` and `CanvasAnchor`, so it is most likely present).

- [ ] **Step 4: Build the solution** — `dotnet build DiffusionNexus.sln --nologo -v q`. Expected: 0 errors (XAML compile included).

- [ ] **Step 5: Commit + push**

```bash
git add DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml.cs
git commit -m "feat(editor): Move / Transform toolbar toggle, panel and view wiring

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
```

---

### Task 12: Docs, full suite, CRLF check, PR

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/ARCHITECTURE.md`, `DiffusionNexus.UI/Doc/Shortcuts.md`, `docs/CHANGELOG.md`

- [ ] **Step 1: ARCHITECTURE.md** (the file is cp1252-encoded in places; edit with a tool that preserves bytes, or convert the whole file to UTF-8 deliberately and say so in the commit):

Under **Overview**, after the first paragraph:

```markdown
Layers have their own bounds: `Layer.OffsetX/OffsetY` place a layer in canvas pixels and its
bitmap may be any size. The compositor draws at the offset and clips to the canvas; Crop clips
layers, Canvas Extend grows them to the union with the canvas, Merge Down unions both layers,
rotate/flip remap offsets (`LayerOffsetRemap`). Content dragged past the canvas edge by the
Move / Transform tool therefore survives. The inpaint mask layer is always canvas-sized at (0, 0).
```

Under **Data Flow Examples**, after Canvas Extend:

```markdown
### Move / Transform
```
User → Move toggle → LayerTransformViewModel.IsPanelOpen = true
     → View sets ImageEditorControl.IsLayerTransformToolActive → LayerTransformTool.IsActive, core ArmLayerTransform()
     → drag body / corner / edge / rotate handle, arrows, panel fields → tool state (translation, scale, rotation, flips)
       → TransformChanged → View → ViewModel.UpdateFromTool (X/Y/W/H/rotation)
     → RenderWithZoom passes LayerRenderOverride(layer, tool.Matrix) to the compositor (live preview);
       the tool draws the off-canvas part at 50 % plus handles
     → Apply (button / Enter) → ImageEditorCore.ApplyLayerTransform()
     → one resample into a bitmap sized to the transformed bounds → Layer.AdoptBitmap(bitmap, offset) → ImageChanged
     → Escape resets (tool stays open); switching tool or layer commits first
```
```

File inventory rows:

```markdown
| `LayerTransformTool.cs` | Move / Transform tool: Shape-style handles on the active layer, canvas-space matrix, commit-on-deactivate |
| `LayerOffsetRemap.cs` | Where a layer's offset lands after a whole-image rotate/flip |
| `ImageEditorCore.LayerTransform.cs` | Arm / eligibility / `ApplyLayerTransform` rasterization with size guard (partial) |
```

Test coverage rows: `LayerBoundsTests.cs`, `LayerCompositorOffsetTests.cs`, `LayerStackOffsetTests.cs`, `TiffExporterOffsetTests.cs`, `LayerTransformToolTests.cs`, `ImageEditorCoreLayerTransformTests.cs`, `ImageEditorCoreOffsetLayerTests.cs`, `LayerTransformViewModelTests.cs` with one-line descriptions.

- [ ] **Step 2: Shortcuts.md** — add to the Image Editor table:

```markdown
| Enter | Apply the layer transform | Move tool active |
| Escape | Reset the layer transform (tool stays open) | Move tool active |
| Arrow keys | Nudge the layer 1 px (Shift: 10 px) | Move tool active |
| Ctrl (held) | Invert "Keep aspect" for this corner drag | Move tool, dragging a corner |
| Shift (held) | Snap rotation to 15° | Move tool, dragging the rotate handle |
```

- [ ] **Step 3: CHANGELOG.md** — under `## Unreleased`: `- **New Feature**: Image Editor Move / Transform tool (#568) — move, scale, rotate and flip the active layer; layers keep content outside the canvas.`

- [ ] **Step 4: Full test suite + solution build**

```bash
dotnet build DiffusionNexus.sln --nologo -v q
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --nologo -v q
```

Expected: 0 errors, all tests pass (the suite had four known flaky tests unrelated to the editor; re-run a failure once before investigating).

- [ ] **Step 5: CRLF check**

```bash
git diff --numstat origin/develop...HEAD | sort -k3 > /tmp/n.txt
git diff -w --numstat origin/develop...HEAD | sort -k3 > /tmp/w.txt
diff /tmp/n.txt /tmp/w.txt
```

Any pre-existing file whose line counts differ wildly between the two = line-ending flip; restore CRLF on that file only and amend into a fix commit.

- [ ] **Step 6: Commit, push, open the PR**

```bash
git add DiffusionNexus.UI/ImageEditor/ARCHITECTURE.md DiffusionNexus.UI/Doc/Shortcuts.md docs/CHANGELOG.md
git commit -m "docs(editor): Move / Transform tool, layer bounds, shortcuts

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push
gh pr create --base develop --title "feat(editor): Move / Transform tool for layers (#568)" --body-file - <<'EOF'
Closes #568.

Move, scale, rotate and flip the active layer with Shape-style handles. Layers gain an offset and their own size (GIMP model), so content dragged past the canvas edge survives; the compositor clips to the canvas.

Spec: docs/superpowers/specs/2026-09-13-layer-transform-tool-design.md
Plan: docs/superpowers/plans/2026-09-13-layer-transform-tool.md

**Manual GUI smoke owed before merge:** drag / scale / rotate / flip on a real image, off-canvas dimming, Enter / Escape / arrows, tool switch commits, layer switch commits, Crop and Extend after a move, TIFF save + reload of a moved layer, mask-layer hint.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

- [ ] **Step 7: Manual GUI smoke** — run the app (`dotnet run --project DiffusionNexus.csproj` or the `run` skill), open an image in the Image Editor, and walk the list in the PR body. Record the result in a PR comment. Anything that fails goes back through the relevant task with a regression test first.
