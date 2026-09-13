using DiffusionNexus.UI.Services;
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
        var bounds = new SKRectI((int)MathF.Floor(boundsF.Left), (int)MathF.Floor(boundsF.Top), (int)MathF.Ceiling(boundsF.Right), (int)MathF.Ceiling(boundsF.Bottom));
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
                try
                {
                    result.Erase(SKColors.Transparent);
                    using var canvas = new SKCanvas(result);
                    canvas.Translate(-bounds.Left, -bounds.Top);
                    canvas.Concat(matrix);
                    using var paint = new SKPaint { IsAntialias = true };
                    using var source = SKImage.FromBitmap(layer.Bitmap);
                    canvas.DrawImage(source, layer.OffsetX, layer.OffsetY, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
                offset = new SKPointI(bounds.Left, bounds.Top);
            }

            lock (_bitmapLock)
            {
                replaced = layer.AdoptBitmapKeepingOld(result, offset);
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
