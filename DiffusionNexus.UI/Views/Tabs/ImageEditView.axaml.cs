using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Image Edit tab in the LoRA Dataset Helper.
/// Handles wiring up the ImageEditorControl events to the ViewModel.
/// </summary>
public partial class ImageEditView : UserControl
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif"];
    
    private static int _instanceCounter;
    private readonly int _instanceId;
    private ImageEditorControl? _imageEditorCanvas;
    private Border? _imageDropZone;
    private Button? _openImageButton;
    private bool _eventsWired;
    private long _lastSyncedInpaintBaseVersion = -1;
    private ImageEditorViewModel? _wiredImageEditor;
    private readonly List<Action> _eventCleanup = [];


    public ImageEditView()
    {
        _instanceId = Interlocked.Increment(ref _instanceCounter);
        FileLogger.Log($"ImageEditView instance #{_instanceId} created - Stack: {Environment.StackTrace}");
        
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _imageEditorCanvas = this.FindControl<ImageEditorControl>("ImageEditorCanvas");
        _imageDropZone = this.FindControl<Border>("ImageDropZone");
        _openImageButton = this.FindControl<Button>("OpenImageButton");
        // Drag-drop and button handlers are registered in OnAttachedToVisualTree
        // so they survive TabControl detach/reattach cycles.
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        FileLogger.Log($"[Instance #{_instanceId}] OnDataContextChanged called, _eventsWired={_eventsWired}, DataContext type={(DataContext?.GetType().Name ?? "null")}");

        // Ignore null DataContext caused by binding deactivation during tab switches.
        // The same View/ViewModel pair is reused, so events should stay connected.
        if (DataContext is null)
            return;

        if (DataContext is ImageEditTabViewModel vm)
        {
            // Only unwire if the DataContext genuinely changed to a different ViewModel.
            if (_wiredImageEditor is not null && _wiredImageEditor != vm.ImageEditor)
                UnwireEvents();

            if (!_eventsWired)
                TryWireUpImageEditorEvents(vm);
        }
    }

    /// <summary>
    /// Unsubscribes all tracked event handlers and clears ViewModel callbacks.
    /// </summary>
    private void UnwireEvents()
    {
        foreach (var cleanup in _eventCleanup)
            cleanup();
        _eventCleanup.Clear();

        if (_wiredImageEditor is not null)
        {
            _wiredImageEditor.SaveImageFunc = null;
            _wiredImageEditor.ShowSaveFileDialogFunc = null;
            _wiredImageEditor = null;
        }

        _eventsWired = false;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Re-register infrastructure handlers removed during detach.
        if (_imageDropZone is not null)
        {
            _imageDropZone.AddHandler(DragDrop.DropEvent, OnImageDrop);
            _imageDropZone.AddHandler(DragDrop.DragEnterEvent, OnImageDragEnter);
            _imageDropZone.AddHandler(DragDrop.DragLeaveEvent, OnImageDragLeave);
        }

        if (_openImageButton is not null)
            _openImageButton.Click += OnOpenImageButtonClick;

        // Re-wire ViewModel events if DataContext is already available.
        if (!_eventsWired && DataContext is ImageEditTabViewModel vm)
            TryWireUpImageEditorEvents(vm);

        // Force canvas redraw so the image reappears after reattach.
        _imageEditorCanvas?.InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Only remove infrastructure handlers; ViewModel events stay connected
        // because the same control/ViewModel pair is reused across tab switches.
        if (_imageDropZone is not null)
        {
            _imageDropZone.RemoveHandler(DragDrop.DropEvent, OnImageDrop);
            _imageDropZone.RemoveHandler(DragDrop.DragEnterEvent, OnImageDragEnter);
            _imageDropZone.RemoveHandler(DragDrop.DragLeaveEvent, OnImageDragLeave);
        }

        if (_openImageButton is not null)
            _openImageButton.Click -= OnOpenImageButtonClick;
    }

    private void TryWireUpImageEditorEvents(ImageEditTabViewModel vm)
    {
        FileLogger.Log($"[Instance #{_instanceId}] TryWireUpImageEditorEvents called, _eventsWired={_eventsWired}");
        
        if (_eventsWired)
        {
            FileLogger.Log($"[Instance #{_instanceId}] Already wired in TryWireUp, skipping");
            return;
        }

        _imageEditorCanvas ??= this.FindControl<ImageEditorControl>("ImageEditorCanvas");

        if (_imageEditorCanvas is not null)
        {
            WireUpImageEditorEvents(vm);
        }
        else
        {
            FileLogger.Log($"[Instance #{_instanceId}] Canvas not found, subscribing to LayoutUpdated");
            LayoutUpdated += OnLayoutUpdatedForEditorInit;
        }
    }

    private void OnLayoutUpdatedForEditorInit(object? sender, EventArgs e)
    {
        FileLogger.Log($"[Instance #{_instanceId}] OnLayoutUpdatedForEditorInit called, _eventsWired={_eventsWired}");
        
        if (_eventsWired)
        {
            LayoutUpdated -= OnLayoutUpdatedForEditorInit;
            return;
        }

        _imageEditorCanvas ??= this.FindControl<ImageEditorControl>("ImageEditorCanvas");

        if (_imageEditorCanvas is not null && DataContext is ImageEditTabViewModel vm)
        {
            LayoutUpdated -= OnLayoutUpdatedForEditorInit;
            WireUpImageEditorEvents(vm);
        }
    }

    private void WireUpImageEditorEvents(ImageEditTabViewModel vm)
    {
        FileLogger.Log($">>> WireUpImageEditorEvents ENTRY - Instance #{_instanceId}, _eventsWired={_eventsWired}");
        
        if (_eventsWired)
        {
            FileLogger.LogWarning($"[Instance #{_instanceId}] WireUpImageEditorEvents called but already wired! Skipping.");
            return;
        }
        
        if (_imageEditorCanvas is null)
        {
            FileLogger.LogWarning($"[Instance #{_instanceId}] WireUpImageEditorEvents called but canvas is null! Skipping.");
            return;
        }
        
        _eventsWired = true;
        
        var imageEditor = vm.ImageEditor;
        _wiredImageEditor = imageEditor;
        
        // Wire the shared EditorServices into the control's core
        _imageEditorCanvas.SetEditorServices(imageEditor.Services);
        
        FileLogger.Log($"[Instance #{_instanceId}] Wiring events for CurrentImagePath={imageEditor.CurrentImagePath ?? "(null)"}");


        WireCanvasEvents(imageEditor);
        WireCropEvents(imageEditor);
        WireZoomAndTransformEvents(imageEditor);
        WireColorToolEvents(imageEditor);
        WireBackgroundRemovalEvents(imageEditor);
        WireBackgroundFillEvents(imageEditor);
        WireUpscalingEvents(imageEditor);
        WireDrawingEvents(imageEditor);
        WireTextToolEvents(imageEditor);
        WireInpaintingEvents(imageEditor);
        WireOutpaintingEvents(imageEditor);
        WireSaveAndExportEvents(vm, imageEditor);
        WireLayerEvents(vm, imageEditor);
        WireZoomSlider();
    }

    private void WireCanvasEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler onImageChanged = (_, _) =>
        {
            imageEditor.UpdateDimensions(
                _imageEditorCanvas!.ImageWidth,
                _imageEditorCanvas.ImageHeight);
            imageEditor.UpdateFileInfo(
                _imageEditorCanvas.ImageDpi,
                _imageEditorCanvas.FileSizeBytes);
            
            imageEditor.LayerPanel.IsLayerMode = _imageEditorCanvas.EditorCore.IsLayerMode;
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);

            var coreVersion = _imageEditorCanvas.EditorCore.InpaintBaseVersion;
            if (coreVersion != _lastSyncedInpaintBaseVersion)
            {
                _lastSyncedInpaintBaseVersion = coreVersion;
                imageEditor.Inpainting.UpdateBaseThumbnail(
                    _imageEditorCanvas.EditorCore.HasInpaintBase
                        ? CreateThumbnailFromEditorCore(_imageEditorCanvas.EditorCore)
                        : null);
            }
        };
        _imageEditorCanvas!.ImageChanged += onImageChanged;
        _eventCleanup.Add(() => _imageEditorCanvas!.ImageChanged -= onImageChanged);

        EventHandler onZoomChanged = (_, _) =>
        {
            imageEditor.UpdateZoomInfo(
                _imageEditorCanvas!.ZoomPercentage,
                _imageEditorCanvas.IsFitMode);
        };
        _imageEditorCanvas.ZoomChanged += onZoomChanged;
        _eventCleanup.Add(() => _imageEditorCanvas!.ZoomChanged -= onZoomChanged);

        EventHandler onCropApplied = (_, _) => imageEditor.OnCropApplied();
        _imageEditorCanvas.CropApplied += onCropApplied;
        _eventCleanup.Add(() => _imageEditorCanvas!.CropApplied -= onCropApplied);
    }

    private void WireCropEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler onClear = (_, _) => _imageEditorCanvas!.ClearImage();
        imageEditor.ClearRequested += onClear;
        _eventCleanup.Add(() => imageEditor.ClearRequested -= onClear);

        EventHandler onReset = (_, _) => _imageEditorCanvas!.ResetToOriginal();
        imageEditor.ResetRequested += onReset;
        _eventCleanup.Add(() => imageEditor.ResetRequested -= onReset);

        EventHandler onCropActivated = (_, _) => _imageEditorCanvas!.ActivateCropTool();
        imageEditor.CropToolActivated += onCropActivated;
        _eventCleanup.Add(() => imageEditor.CropToolActivated -= onCropActivated);

        EventHandler onCropDeactivated = (_, _) => _imageEditorCanvas!.DeactivateCropTool();
        imageEditor.CropToolDeactivated += onCropDeactivated;
        _eventCleanup.Add(() => imageEditor.CropToolDeactivated -= onCropDeactivated);

        EventHandler onApplyCrop = (_, _) =>
        {
            if (_imageEditorCanvas!.ApplyCrop())
                imageEditor.OnCropApplied();
        };
        imageEditor.ApplyCropRequested += onApplyCrop;
        _eventCleanup.Add(() => imageEditor.ApplyCropRequested -= onApplyCrop);

        EventHandler onCancelCrop = (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.ClearCropRegion();
            _imageEditorCanvas.DeactivateCropTool();
        };
        imageEditor.CancelCropRequested += onCancelCrop;
        _eventCleanup.Add(() => imageEditor.CancelCropRequested -= onCancelCrop);

        EventHandler onFitCrop = (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.FitToImage();
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.FitCropRequested += onFitCrop;
        _eventCleanup.Add(() => imageEditor.FitCropRequested -= onFitCrop);

        EventHandler onFillCrop = (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.FillImage();
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.FillCropRequested += onFillCrop;
        _eventCleanup.Add(() => imageEditor.FillCropRequested -= onFillCrop);

        EventHandler<(float W, float H)> onSetAspect = (_, ratio) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.SetAspectRatio(ratio.W, ratio.H);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.SetCropAspectRatioRequested += onSetAspect;
        _eventCleanup.Add(() => imageEditor.SetCropAspectRatioRequested -= onSetAspect);

        EventHandler onSwitchAspect = (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.SwitchAspectRatio();
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.SwitchCropAspectRatioRequested += onSwitchAspect;
        _eventCleanup.Add(() => imageEditor.SwitchCropAspectRatioRequested -= onSwitchAspect);

        EventHandler onCropRegionChanged = (_, _) =>
        {
            var (w, h) = _imageEditorCanvas!.EditorCore.CropTool.GetCropPixelDimensions();
            imageEditor.UpdateCropResolution(w, h);
        };
        _imageEditorCanvas!.EditorCore.CropTool.CropRegionChanged += onCropRegionChanged;
        _eventCleanup.Add(() => _imageEditorCanvas!.EditorCore.CropTool.CropRegionChanged -= onCropRegionChanged);
    }

    private void WireOutpaintingEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler onOutpaintActivated = (_, _) =>
        {
            _imageEditorCanvas!.IsOutpaintToolActive = true;
        };
        imageEditor.Outpainting.OutpaintToolActivated += onOutpaintActivated;
        _eventCleanup.Add(() => imageEditor.Outpainting.OutpaintToolActivated -= onOutpaintActivated);

        EventHandler onOutpaintDeactivated = (_, _) =>
        {
            _imageEditorCanvas!.IsOutpaintToolActive = false;
            _imageEditorCanvas!.EditorCore.OutpaintTool.Reset();
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.Outpainting.OutpaintToolDeactivated += onOutpaintDeactivated;
        _eventCleanup.Add(() => imageEditor.Outpainting.OutpaintToolDeactivated -= onOutpaintDeactivated);

        EventHandler onResetOutpaint = (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.OutpaintTool.Reset();
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.Outpainting.ResetRequested += onResetOutpaint;
        _eventCleanup.Add(() => imageEditor.Outpainting.ResetRequested -= onResetOutpaint);

        EventHandler<(float W, float H)> onSetOutpaintAspect = (_, ratio) =>
        {
            _imageEditorCanvas!.EditorCore.OutpaintTool.SetAspectRatio(ratio.W, ratio.H);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.Outpainting.SetAspectRatioRequested += onSetOutpaintAspect;
        _eventCleanup.Add(() => imageEditor.Outpainting.SetAspectRatioRequested -= onSetOutpaintAspect);

        EventHandler onOutpaintRegionChanged = (_, _) =>
        {
            var (w, h) = _imageEditorCanvas!.EditorCore.OutpaintTool.GetNewDimensions();
            imageEditor.Outpainting.UpdateResolution(w, h);
        };
        _imageEditorCanvas!.OutpaintRegionChanged += onOutpaintRegionChanged;
        _eventCleanup.Add(() => _imageEditorCanvas!.OutpaintRegionChanged -= onOutpaintRegionChanged);
    }

    private void WireZoomAndTransformEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler onZoomIn = (_, _) => _imageEditorCanvas!.ZoomIn();
        EventHandler onZoomOut = (_, _) => _imageEditorCanvas!.ZoomOut();
        EventHandler onZoomFit = (_, _) => _imageEditorCanvas!.ZoomToFit();
        EventHandler onZoomActual = (_, _) => _imageEditorCanvas!.ZoomToActual();
        EventHandler onRotateL = (_, _) => _imageEditorCanvas!.EditorCore.RotateLeft();
        EventHandler onRotateR = (_, _) => _imageEditorCanvas!.EditorCore.RotateRight();
        EventHandler onRotate180 = (_, _) => _imageEditorCanvas!.EditorCore.Rotate180();
        EventHandler onFlipH = (_, _) => _imageEditorCanvas!.EditorCore.FlipHorizontal();
        EventHandler onFlipV = (_, _) => _imageEditorCanvas!.EditorCore.FlipVertical();

        imageEditor.ZoomInRequested += onZoomIn;
        imageEditor.ZoomOutRequested += onZoomOut;
        imageEditor.ZoomToFitRequested += onZoomFit;
        imageEditor.ZoomToActualRequested += onZoomActual;
        imageEditor.RotateLeftRequested += onRotateL;
        imageEditor.RotateRightRequested += onRotateR;
        imageEditor.Rotate180Requested += onRotate180;
        imageEditor.FlipHorizontalRequested += onFlipH;
        imageEditor.FlipVerticalRequested += onFlipV;

        _eventCleanup.Add(() => imageEditor.ZoomInRequested -= onZoomIn);
        _eventCleanup.Add(() => imageEditor.ZoomOutRequested -= onZoomOut);
        _eventCleanup.Add(() => imageEditor.ZoomToFitRequested -= onZoomFit);
        _eventCleanup.Add(() => imageEditor.ZoomToActualRequested -= onZoomActual);
        _eventCleanup.Add(() => imageEditor.RotateLeftRequested -= onRotateL);
        _eventCleanup.Add(() => imageEditor.RotateRightRequested -= onRotateR);
        _eventCleanup.Add(() => imageEditor.Rotate180Requested -= onRotate180);
        _eventCleanup.Add(() => imageEditor.FlipHorizontalRequested -= onFlipH);
        _eventCleanup.Add(() => imageEditor.FlipVerticalRequested -= onFlipV);
    }

    private void WireColorToolEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler<ColorBalanceSettings> onApplyCB = (_, settings) =>
        {
            _imageEditorCanvas!.EditorCore.ClearPreview();
            if (_imageEditorCanvas.EditorCore.ApplyColorBalance(settings))
                imageEditor.OnColorBalanceApplied();
            else
                imageEditor.StatusMessage = "Failed to apply color balance.";
        };
        imageEditor.ColorTools.ApplyColorBalanceRequested += onApplyCB;
        _eventCleanup.Add(() => imageEditor.ColorTools.ApplyColorBalanceRequested -= onApplyCB);

        EventHandler<ColorBalanceSettings> onPreviewCB = (_, settings) =>
            _imageEditorCanvas!.EditorCore.SetColorBalancePreview(settings);
        imageEditor.ColorTools.ColorBalancePreviewRequested += onPreviewCB;
        _eventCleanup.Add(() => imageEditor.ColorTools.ColorBalancePreviewRequested -= onPreviewCB);

        EventHandler onCancelCB = (_, _) => _imageEditorCanvas!.EditorCore.ClearPreview();
        imageEditor.ColorTools.CancelColorBalancePreviewRequested += onCancelCB;
        _eventCleanup.Add(() => imageEditor.ColorTools.CancelColorBalancePreviewRequested -= onCancelCB);

        EventHandler<BrightnessContrastSettings> onApplyBC = (_, settings) =>
        {
            _imageEditorCanvas!.EditorCore.ClearPreview();
            if (_imageEditorCanvas.EditorCore.ApplyBrightnessContrast(settings))
                imageEditor.OnBrightnessContrastApplied();
            else
                imageEditor.StatusMessage = "Failed to apply brightness/contrast.";
        };
        imageEditor.ColorTools.ApplyBrightnessContrastRequested += onApplyBC;
        _eventCleanup.Add(() => imageEditor.ColorTools.ApplyBrightnessContrastRequested -= onApplyBC);

        EventHandler<BrightnessContrastSettings> onPreviewBC = (_, settings) =>
            _imageEditorCanvas!.EditorCore.SetBrightnessContrastPreview(settings);
        imageEditor.ColorTools.BrightnessContrastPreviewRequested += onPreviewBC;
        _eventCleanup.Add(() => imageEditor.ColorTools.BrightnessContrastPreviewRequested -= onPreviewBC);

        EventHandler onCancelBC = (_, _) => _imageEditorCanvas!.EditorCore.ClearPreview();
        imageEditor.ColorTools.CancelBrightnessContrastPreviewRequested += onCancelBC;
        _eventCleanup.Add(() => imageEditor.ColorTools.CancelBrightnessContrastPreviewRequested -= onCancelBC);
    }

    private void WireBackgroundRemovalEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler onRemoveBg = async (_, _) =>
        {
            var imageData = _imageEditorCanvas!.EditorCore.GetWorkingBitmapData();
            if (imageData is null) { imageEditor.StatusMessage = "No image loaded"; return; }

            await imageEditor.BackgroundRemoval.ProcessBackgroundRemovalAsync(
                imageData.Value.Data, imageData.Value.Width, imageData.Value.Height);
        };
        imageEditor.BackgroundRemoval.RemoveBackgroundRequested += onRemoveBg;
        _eventCleanup.Add(() => imageEditor.BackgroundRemoval.RemoveBackgroundRequested -= onRemoveBg);

        EventHandler<BackgroundRemovalResult> onBgCompleted = (_, result) =>
        {
            if (result.Success && result.MaskData is not null)
            {
                if (_imageEditorCanvas!.EditorCore.ApplyBackgroundMask(result.MaskData, result.Width, result.Height))
                    imageEditor.BackgroundRemoval.OnBackgroundRemovalApplied();
                else
                    imageEditor.StatusMessage = "Failed to apply background removal mask";
            }
        };
        imageEditor.BackgroundRemoval.BackgroundRemovalCompleted += onBgCompleted;
        _eventCleanup.Add(() => imageEditor.BackgroundRemoval.BackgroundRemovalCompleted -= onBgCompleted);

        EventHandler onRemoveBgToLayer = async (_, _) =>
        {
            var imageData = _imageEditorCanvas!.EditorCore.GetWorkingBitmapData();
            if (imageData is null) { imageEditor.StatusMessage = "No image loaded"; return; }

            await imageEditor.BackgroundRemoval.ProcessBackgroundRemovalToLayerAsync(
                imageData.Value.Data, imageData.Value.Width, imageData.Value.Height);
        };
        imageEditor.BackgroundRemoval.RemoveBackgroundToLayerRequested += onRemoveBgToLayer;
        _eventCleanup.Add(() => imageEditor.BackgroundRemoval.RemoveBackgroundToLayerRequested -= onRemoveBgToLayer);

        EventHandler<BackgroundRemovalResult> onBgLayerCompleted = (_, result) =>
        {
            if (result.Success && result.MaskData is not null)
            {
                if (_imageEditorCanvas!.EditorCore.ApplyBackgroundMaskWithLayers(result.MaskData, result.Width, result.Height))
                {
                    imageEditor.BackgroundRemoval.OnBackgroundRemovalToLayerApplied();
                    imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
                }
                else
                {
                    imageEditor.StatusMessage = "Failed to create layers from background removal mask";
                }
            }
        };
        imageEditor.BackgroundRemoval.BackgroundRemovalToLayerCompleted += onBgLayerCompleted;
        _eventCleanup.Add(() => imageEditor.BackgroundRemoval.BackgroundRemovalToLayerCompleted -= onBgLayerCompleted);
    }

    private void WireBackgroundFillEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler<BackgroundFillSettings> onPreview = (_, settings) =>
            _imageEditorCanvas!.EditorCore.SetBackgroundFillPreview(settings);
        imageEditor.BackgroundFill.PreviewRequested += onPreview;
        _eventCleanup.Add(() => imageEditor.BackgroundFill.PreviewRequested -= onPreview);

        EventHandler onCancelPreview = (_, _) => _imageEditorCanvas!.EditorCore.ClearPreview();
        imageEditor.BackgroundFill.CancelPreviewRequested += onCancelPreview;
        _eventCleanup.Add(() => imageEditor.BackgroundFill.CancelPreviewRequested -= onCancelPreview);

        EventHandler<BackgroundFillSettings> onApply = (_, settings) =>
        {
            _imageEditorCanvas!.EditorCore.ClearPreview();
            if (_imageEditorCanvas.EditorCore.ApplyBackgroundFill(settings))
                imageEditor.BackgroundFill.OnFillApplied();
            else
                imageEditor.StatusMessage = "Failed to apply background fill";
        };
        imageEditor.BackgroundFill.ApplyRequested += onApply;
        _eventCleanup.Add(() => imageEditor.BackgroundFill.ApplyRequested -= onApply);
    }

    private void WireUpscalingEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler onUpscale = async (_, _) =>
        {
            var imageData = _imageEditorCanvas!.EditorCore.GetWorkingBitmapData();
            if (imageData is null) { imageEditor.StatusMessage = "No image loaded"; return; }

            await imageEditor.Upscaling.ProcessUpscalingAsync(
                imageData.Value.Data, imageData.Value.Width, imageData.Value.Height);
        };
        imageEditor.Upscaling.UpscaleRequested += onUpscale;
        _eventCleanup.Add(() => imageEditor.Upscaling.UpscaleRequested -= onUpscale);

        EventHandler<ImageUpscalingResult> onUpscaleCompleted = (_, result) =>
        {
            if (result.Success && result.ImageData is not null)
            {
                if (_imageEditorCanvas!.EditorCore.LoadImage(result.ImageData))
                {
                    imageEditor.Upscaling.OnUpscalingApplied();
                    imageEditor.UpdateDimensions(
                        _imageEditorCanvas.ImageWidth, _imageEditorCanvas.ImageHeight);
                }
                else
                {
                    imageEditor.StatusMessage = "Failed to load upscaled image";
                }
            }
        };
        imageEditor.Upscaling.UpscalingCompleted += onUpscaleCompleted;
        _eventCleanup.Add(() => imageEditor.Upscaling.UpscalingCompleted -= onUpscaleCompleted);
    }

    private void WireDrawingEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler<bool> onDrawingActivated = (_, isActive) =>
        {
            var drawingTool = _imageEditorCanvas!.EditorCore.DrawingTool;
            drawingTool.IsActive = isActive;
            if (isActive)
                ApplyDrawingSettingsToTool(imageEditor, drawingTool);
        };
        imageEditor.DrawingTools.DrawingToolActivated += onDrawingActivated;
        _eventCleanup.Add(() => imageEditor.DrawingTools.DrawingToolActivated -= onDrawingActivated);

        EventHandler<ImageEditor.DrawingSettings> onSettingsChanged = (_, _) =>
        {
            var drawingTool = _imageEditorCanvas!.EditorCore.DrawingTool;
            drawingTool.BrushColor = new SkiaSharp.SKColor(
                imageEditor.DrawingTools.DrawingBrushRed,
                imageEditor.DrawingTools.DrawingBrushGreen,
                imageEditor.DrawingTools.DrawingBrushBlue);
            drawingTool.BrushSize = imageEditor.DrawingTools.DrawingBrushSize;
            drawingTool.BrushShape = imageEditor.DrawingTools.DrawingBrushShape;
        };
        imageEditor.DrawingTools.DrawingSettingsChanged += onSettingsChanged;
        _eventCleanup.Add(() => imageEditor.DrawingTools.DrawingSettingsChanged -= onSettingsChanged);

        EventHandler onCommitShape = (_, _) => _imageEditorCanvas!.CommitPlacedShape();
        imageEditor.DrawingTools.CommitPlacedShapeRequested += onCommitShape;
        _eventCleanup.Add(() => imageEditor.DrawingTools.CommitPlacedShapeRequested -= onCommitShape);

        EventHandler onCancelShape = (_, _) => _imageEditorCanvas!.CancelPlacedShape();
        imageEditor.DrawingTools.CancelPlacedShapeRequested += onCancelShape;
        _eventCleanup.Add(() => imageEditor.DrawingTools.CancelPlacedShapeRequested -= onCancelShape);

        EventHandler onPlacedShapeState = (_, _) =>
            imageEditor.DrawingTools.HasPlacedShape = _imageEditorCanvas!.HasPlacedShape;
        _imageEditorCanvas!.PlacedShapeStateChanged += onPlacedShapeState;
        _eventCleanup.Add(() => _imageEditorCanvas!.PlacedShapeStateChanged -= onPlacedShapeState);
    }

    private void WireTextToolEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler<bool> onTextToolActivated = (_, isActive) =>
        {
            _imageEditorCanvas!.IsTextToolActive = isActive;
            if (isActive)
                ApplyTextSettingsToTool(imageEditor, _imageEditorCanvas.EditorCore.TextTool);
        };
        imageEditor.TextTools.TextToolActivated += onTextToolActivated;
        _eventCleanup.Add(() => imageEditor.TextTools.TextToolActivated -= onTextToolActivated);

        EventHandler onTextSettingsChanged = (_, _) =>
        {
            var textTool = _imageEditorCanvas!.EditorCore.TextTool;
            ApplyTextSettingsToTool(imageEditor, textTool);
            textTool.UpdatePlacedTextProperties();
        };
        imageEditor.TextTools.TextSettingsChanged += onTextSettingsChanged;
        _eventCleanup.Add(() => imageEditor.TextTools.TextSettingsChanged -= onTextSettingsChanged);

        EventHandler onPlaceText = (_, _) =>
        {
            var textTool = _imageEditorCanvas!.EditorCore.TextTool;
            ApplyTextSettingsToTool(imageEditor, textTool);
            textTool.PlaceText();
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.TextTools.PlaceTextRequested += onPlaceText;
        _eventCleanup.Add(() => imageEditor.TextTools.PlaceTextRequested -= onPlaceText);

        EventHandler onCommitText = (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.TextTool.CommitPlacedText();
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.TextTools.CommitPlacedTextRequested += onCommitText;
        _eventCleanup.Add(() => imageEditor.TextTools.CommitPlacedTextRequested -= onCommitText);

        EventHandler onCancelText = (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.TextTool.CancelPlacedText();
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.TextTools.CancelPlacedTextRequested += onCancelText;
        _eventCleanup.Add(() => imageEditor.TextTools.CancelPlacedTextRequested -= onCancelText);

        EventHandler onPlacedTextState = (_, _) =>
            imageEditor.TextTools.HasPlacedText = _imageEditorCanvas!.HasPlacedText;
        _imageEditorCanvas!.PlacedTextStateChanged += onPlacedTextState;
        _eventCleanup.Add(() => _imageEditorCanvas!.PlacedTextStateChanged -= onPlacedTextState);
    }

    private void WireInpaintingEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler onSetBase = (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.SetInpaintBaseBitmap();
            _lastSyncedInpaintBaseVersion = _imageEditorCanvas.EditorCore.InpaintBaseVersion;
            imageEditor.Inpainting.UpdateBaseThumbnail(CreateThumbnailFromEditorCore(_imageEditorCanvas.EditorCore));
        };
        imageEditor.Inpainting.SetBaseRequested += onSetBase;
        _eventCleanup.Add(() => imageEditor.Inpainting.SetBaseRequested -= onSetBase);

        EventHandler<bool> onToolActivated = (_, isActive) =>
            _imageEditorCanvas!.IsInpaintingToolActive = isActive;
        imageEditor.Inpainting.ToolActivated += onToolActivated;
        _eventCleanup.Add(() => imageEditor.Inpainting.ToolActivated -= onToolActivated);

        EventHandler onSettingsChanged = (_, _) =>
            _imageEditorCanvas!.InpaintBrushSize = imageEditor.Inpainting.BrushSize;
        imageEditor.Inpainting.SettingsChanged += onSettingsChanged;
        _eventCleanup.Add(() => imageEditor.Inpainting.SettingsChanged -= onSettingsChanged);

        EventHandler<float> onBrushSizeChanged = (_, newSize) =>
            imageEditor.Inpainting.BrushSize = newSize;
        _imageEditorCanvas!.InpaintBrushSizeChanged += onBrushSizeChanged;
        _eventCleanup.Add(() => _imageEditorCanvas!.InpaintBrushSizeChanged -= onBrushSizeChanged);

        EventHandler onGenerateRequested = (_, _) =>
        {
            if (imageEditor.Inpainting.GenerateCommand.CanExecute(null))
                imageEditor.Inpainting.GenerateCommand.Execute(null);
        };
        _imageEditorCanvas.InpaintGenerateRequested += onGenerateRequested;
        _eventCleanup.Add(() => _imageEditorCanvas!.InpaintGenerateRequested -= onGenerateRequested);

        var inpaintPromptTextBox = this.FindControl<TextBox>("InpaintPromptTextBox");
        if (inpaintPromptTextBox is not null)
        {
            EventHandler<KeyEventArgs> onKeyDown = (_, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    if (imageEditor.Inpainting.GenerateCommand.CanExecute(null))
                        imageEditor.Inpainting.GenerateCommand.Execute(null);
                    e.Handled = true;
                }
            };
            inpaintPromptTextBox.KeyDown += onKeyDown;
            _eventCleanup.Add(() => inpaintPromptTextBox.KeyDown -= onKeyDown);
        }

        EventHandler onClearMask = (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.ClearInpaintMask();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.Inpainting.ClearMaskRequested += onClearMask;
        _eventCleanup.Add(() => imageEditor.Inpainting.ClearMaskRequested -= onClearMask);

        EventHandler onGenerate = async (_, _) =>
        {
            if (_imageEditorCanvas is null) return;

            var tempPath = string.Empty;
            try
            {
                var editorCore = _imageEditorCanvas.EditorCore;
                var versionBefore = editorCore.InpaintBaseVersion;

                var prepareResult = editorCore.PrepareInpaintMaskedImage(imageEditor.Inpainting.MaskFeather);

                if (editorCore.InpaintBaseVersion != versionBefore)
                {
                    _lastSyncedInpaintBaseVersion = editorCore.InpaintBaseVersion;
                    imageEditor.Inpainting.UpdateBaseThumbnail(CreateThumbnailFromEditorCore(editorCore));
                }

                if (!prepareResult.Success)
                {
                    imageEditor.StatusMessage = prepareResult.ErrorMessage;
                    return;
                }

                // Capture the before PNG synchronously (before any await) so the
                // inpaint base bitmap cannot be modified by another event during a yield.
                byte[]? beforePng = null;
                if (imageEditor.Inpainting.IsCompareModePending)
                {
                    beforePng = editorCore.GetInpaintBaseAsPng();
                }

                tempPath = Path.Combine(Path.GetTempPath(), $"diffnexus_inpaint_{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(tempPath, prepareResult.MaskedImagePng!);

                if (beforePng is not null)
                {
                    var beforePath = Path.Combine(Path.GetTempPath(), $"diffnexus_inpaint_before_{Guid.NewGuid():N}.png");
                    await File.WriteAllBytesAsync(beforePath, beforePng);
                    imageEditor.Inpainting.SetCompareBeforeImagePath(beforePath);
                }

                await imageEditor.Inpainting.ProcessInpaintAsync(tempPath);
            }
            catch (Exception ex)
            {
                imageEditor.StatusMessage = $"Inpainting failed: {ex.Message}";
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            }
        };
        imageEditor.Inpainting.GenerateRequested += onGenerate;
        _eventCleanup.Add(() => imageEditor.Inpainting.GenerateRequested -= onGenerate);

        EventHandler<byte[]> onResultReady = (_, imageBytes) =>
        {
            if (_imageEditorCanvas is null) return;

            var editorCore = _imageEditorCanvas.EditorCore;
            var resultBitmap = SkiaSharp.SKBitmap.Decode(imageBytes);
            if (resultBitmap is null)
            {
                imageEditor.StatusMessage = "Failed to decode inpainting result.";
                return;
            }

            if (resultBitmap.Width != editorCore.Width || resultBitmap.Height != editorCore.Height)
            {
                var resized = new SkiaSharp.SKBitmap(editorCore.Width, editorCore.Height);
                using var canvas = new SkiaSharp.SKCanvas(resized);
                canvas.DrawBitmap(resultBitmap,
                    new SkiaSharp.SKRect(0, 0, editorCore.Width, editorCore.Height));
                resultBitmap.Dispose();
                resultBitmap = resized;
            }

            editorCore.AddLayerFromBitmap(resultBitmap, "Inpaint Result");
            imageEditor.LayerPanel.SyncLayers(editorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.Inpainting.ResultReady += onResultReady;
        _eventCleanup.Add(() => imageEditor.Inpainting.ResultReady -= onResultReady);

        EventHandler onMaskChanged = (_, _) =>
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas!.EditorCore.Layers);
        _imageEditorCanvas.InpaintMaskChanged += onMaskChanged;
        _eventCleanup.Add(() => _imageEditorCanvas!.InpaintMaskChanged -= onMaskChanged);

        EventHandler onHideMask = (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            if (_imageEditorCanvas.EditorCore.SetInpaintMaskVisible(false))
            {
                imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
                _imageEditorCanvas.InvalidateVisual();
            }
        };
        imageEditor.Inpainting.HideMaskRequested += onHideMask;
        _eventCleanup.Add(() => imageEditor.Inpainting.HideMaskRequested -= onHideMask);

        EventHandler onPaintingStarted = (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            if (_imageEditorCanvas.EditorCore.SetInpaintMaskVisible(true))
            {
                imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
                _imageEditorCanvas.InvalidateVisual();
            }
        };
        _imageEditorCanvas.InpaintPaintingStarted += onPaintingStarted;
        _eventCleanup.Add(() => _imageEditorCanvas!.InpaintPaintingStarted -= onPaintingStarted);
    }

    private void WireSaveAndExportEvents(ImageEditTabViewModel vm, ImageEditorViewModel imageEditor)
    {
        // Provide the View's save capability to the ViewModel (cleaned up in UnwireEvents)
        imageEditor.SaveImageFunc = path =>
        {
            _imageEditorCanvas?.EditorCore.CommitPendingOperations();
            return _imageEditorCanvas?.EditorCore.SaveImage(path) ?? false;
        };

        imageEditor.SaveLayeredTiffFunc = path =>
        {
            _imageEditorCanvas?.EditorCore.CommitPendingOperations();
            return _imageEditorCanvas?.EditorCore.SaveLayeredTiff(path) ?? false;
        };

        imageEditor.ShowSaveFileDialogFunc = async (title, suggestedFileName, filter) =>
        {
            if (vm.DialogService is null) return null;
            return await vm.DialogService.ShowSaveFileDialogAsync(title, suggestedFileName, filter);
        };

        Func<Task<SaveAsResult>> onSaveAsDialog = async () =>
        {
            if (vm.DialogService is null || imageEditor.CurrentImagePath is null)
                return SaveAsResult.Cancelled();

            var hasLayers = _imageEditorCanvas?.EditorCore?.Layers?.Count > 1;

            return await vm.DialogService.ShowSaveAsDialogAsync(
                imageEditor.CurrentImagePath,
                vm.EditorDatasets.Where(d => !d.IsTemporary),
                vm.SelectedEditorDataset?.Name,
                vm.SelectedEditorVersion?.Version,
                hasLayers);
        };
        imageEditor.SaveAsDialogRequested += onSaveAsDialog;
        _eventCleanup.Add(() => imageEditor.SaveAsDialogRequested -= onSaveAsDialog);

        Func<Task<bool>> onOverwriteConfirm = async () =>
        {
            if (vm.DialogService is not null)
            {
                return await vm.DialogService.ShowConfirmAsync(
                    "Overwrite Image",
                    "Do you really want to overwrite your original image? This cannot be undone.");
            }
            return false;
        };
        imageEditor.SaveOverwriteConfirmRequested += onOverwriteConfirm;
        _eventCleanup.Add(() => imageEditor.SaveOverwriteConfirmRequested -= onOverwriteConfirm);
    }

    private void WireLayerEvents(ImageEditTabViewModel vm, ImageEditorViewModel imageEditor)
    {
        EventHandler<bool> onEnableLayer = (_, enable) =>
        {
            if (_imageEditorCanvas is null) return;
            if (enable) _imageEditorCanvas.EditorCore.EnableLayerMode();
            else _imageEditorCanvas.EditorCore.DisableLayerMode();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerPanel.EnableLayerModeRequested += onEnableLayer;
        _eventCleanup.Add(() => imageEditor.LayerPanel.EnableLayerModeRequested -= onEnableLayer);

        EventHandler onAddLayer = (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.AddLayer();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerPanel.AddLayerRequested += onAddLayer;
        _eventCleanup.Add(() => imageEditor.LayerPanel.AddLayerRequested -= onAddLayer);

        EventHandler<Layer> onDeleteLayer = (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.RemoveLayer(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerPanel.DeleteLayerRequested += onDeleteLayer;
        _eventCleanup.Add(() => imageEditor.LayerPanel.DeleteLayerRequested -= onDeleteLayer);

        EventHandler<Layer> onDuplicateLayer = (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.DuplicateLayer(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerPanel.DuplicateLayerRequested += onDuplicateLayer;
        _eventCleanup.Add(() => imageEditor.LayerPanel.DuplicateLayerRequested -= onDuplicateLayer);

        EventHandler<Layer> onMoveUp = (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MoveLayerUp(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerPanel.MoveLayerUpRequested += onMoveUp;
        _eventCleanup.Add(() => imageEditor.LayerPanel.MoveLayerUpRequested -= onMoveUp);

        EventHandler<Layer> onMoveDown = (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MoveLayerDown(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerPanel.MoveLayerDownRequested += onMoveDown;
        _eventCleanup.Add(() => imageEditor.LayerPanel.MoveLayerDownRequested -= onMoveDown);

        EventHandler<Layer> onMergeDown = (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MergeLayerDown(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerPanel.MergeLayerDownRequested += onMergeDown;
        _eventCleanup.Add(() => imageEditor.LayerPanel.MergeLayerDownRequested -= onMergeDown);

        EventHandler onMergeVisible = (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MergeVisibleLayers();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerPanel.MergeVisibleLayersRequested += onMergeVisible;
        _eventCleanup.Add(() => imageEditor.LayerPanel.MergeVisibleLayersRequested -= onMergeVisible);

        EventHandler onFlatten = (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.FlattenAllLayers();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.LayerPanel.FlattenLayersRequested += onFlatten;
        _eventCleanup.Add(() => imageEditor.LayerPanel.FlattenLayersRequested -= onFlatten);

        EventHandler<Layer?> onLayerSelection = (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.ActiveLayer = layer;
        };
        imageEditor.LayerPanel.LayerSelectionChanged += onLayerSelection;
        _eventCleanup.Add(() => imageEditor.LayerPanel.LayerSelectionChanged -= onLayerSelection);

        Func<string, Task<bool>> onSaveTiff = async (suggestedPath) =>
        {
            if (_imageEditorCanvas is null || vm.DialogService is null) return false;
            
            var savePath = await vm.DialogService.ShowSaveFileDialogAsync(
                "Save Layered TIFF", Path.GetFileName(suggestedPath), "*.tif");
                
            if (string.IsNullOrEmpty(savePath)) return false;
            return _imageEditorCanvas.EditorCore.SaveLayeredTiff(savePath);
        };
        imageEditor.LayerPanel.SaveLayeredTiffRequested += onSaveTiff;
        _eventCleanup.Add(() => imageEditor.LayerPanel.SaveLayeredTiffRequested -= onSaveTiff);
    }

    private void WireZoomSlider()
    {
        var zoomSlider = this.FindControl<Slider>("ZoomSlider");
        if (zoomSlider is not null)
        {
            EventHandler<AvaloniaPropertyChangedEventArgs> onSliderChanged = (_, args) =>
            {
                if (args.Property.Name == nameof(Slider.Value) && _imageEditorCanvas is not null)
                {
                    var percentage = (int)zoomSlider.Value;
                    _imageEditorCanvas.SetZoom(percentage / 100f);
                }
            };
            zoomSlider.PropertyChanged += onSliderChanged;
            _eventCleanup.Add(() => zoomSlider.PropertyChanged -= onSliderChanged);
        }
    }

    /// <summary>
    /// Applies the current drawing settings from the ViewModel to the drawing tool.
    /// </summary>
    private static void ApplyDrawingSettingsToTool(ImageEditorViewModel imageEditor, ImageEditor.DrawingTool drawingTool)
    {
        drawingTool.BrushColor = new SkiaSharp.SKColor(
            imageEditor.DrawingTools.DrawingBrushRed,
            imageEditor.DrawingTools.DrawingBrushGreen,
            imageEditor.DrawingTools.DrawingBrushBlue);
        drawingTool.BrushSize = imageEditor.DrawingTools.DrawingBrushSize;
        drawingTool.BrushShape = imageEditor.DrawingTools.DrawingBrushShape;
    }

    /// <summary>
    /// Applies the current text settings from the ViewModel to the text tool.
    /// </summary>
    private static void ApplyTextSettingsToTool(ImageEditorViewModel imageEditor, ImageEditor.TextTool textTool)
    {
        textTool.Text = imageEditor.TextTools.Text;
        textTool.FontFamily = imageEditor.TextTools.FontFamily;
        textTool.FontSize = imageEditor.TextTools.FontSize;
        textTool.IsBold = imageEditor.TextTools.IsBold;
        textTool.IsItalic = imageEditor.TextTools.IsItalic;
        textTool.TextColor = new SkiaSharp.SKColor(
            imageEditor.TextTools.TextColorRed,
            imageEditor.TextTools.TextColorGreen,
            imageEditor.TextTools.TextColorBlue);
        textTool.OutlineColor = new SkiaSharp.SKColor(
            imageEditor.TextTools.OutlineColorRed,
            imageEditor.TextTools.OutlineColorGreen,
            imageEditor.TextTools.OutlineColorBlue);
        textTool.OutlineWidth = imageEditor.TextTools.OutlineWidth;
    }

    /// <summary>
    /// Creates a small Avalonia thumbnail bitmap from the EditorCore's current inpaint base.
    /// </summary>
    private static Avalonia.Media.Imaging.Bitmap? CreateThumbnailFromEditorCore(ImageEditor.ImageEditorCore editorCore)
    {
        var baseBitmap = editorCore.GetInpaintBaseBitmap();
        if (baseBitmap is null) return null;

        try
        {
            using var image = SkiaSharp.SKImage.FromBitmap(baseBitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 80);
            baseBitmap.Dispose();

            using var memoryStream = new MemoryStream();
            data.SaveTo(memoryStream);
            memoryStream.Position = 0;
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(memoryStream, 160);
        }
        catch
        {
            baseBitmap.Dispose();
            return null;
        }
    }

    #region Image Drop Zone Handlers

    private void OnImageDragEnter(object? sender, DragEventArgs e)
    {
        if (_imageDropZone is null) return;

        var hasValidImage = AnalyzeImageFilesInDrag(e);

        if (hasValidImage)
        {
            _imageDropZone.BorderBrush = Brushes.LimeGreen;
            _imageDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            _imageDropZone.BorderBrush = Brushes.Red;
            _imageDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnImageDragLeave(object? sender, DragEventArgs e)
    {
        if (_imageDropZone is not null)
        {
            _imageDropZone.BorderBrush = new SolidColorBrush(Color.Parse("#444"));
            _imageDropZone.BorderThickness = new Thickness(3);
        }
    }

    private void OnImageDrop(object? sender, DragEventArgs e)
    {
        // Reset border
        if (_imageDropZone is not null)
        {
            _imageDropZone.BorderBrush = new SolidColorBrush(Color.Parse("#444"));
            _imageDropZone.BorderThickness = new Thickness(3);
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        // Find the first valid image file
        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var filePath = file.Path.LocalPath;
                if (IsImageFile(filePath))
                {
                    LoadDroppedImage(filePath);
                    return;
                }
            }
        }
    }

    private bool AnalyzeImageFilesInDrag(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return false;

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var filePath = file.Path.LocalPath;
                if (IsImageFile(filePath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsImageFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return ImageExtensions.Contains(extension);
    }

    private static bool IsTiffFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".tiff" or ".tif";
    }

    private void LoadDroppedImage(string filePath)
    {
        if (_imageEditorCanvas is null || DataContext is not ImageEditTabViewModel vm)
            return;

        try
        {
            FileLogger.Log($"Loading dropped image: {filePath}");
            
            bool loaded;
            
            // TIFF files are loaded as layers to preserve multi-page structure
            if (IsTiffFile(filePath))
            {
                FileLogger.Log($"Detected TIFF file, loading as layers: {filePath}");
                loaded = _imageEditorCanvas.LoadLayeredTiff(filePath);
            }
            else
            {
                loaded = _imageEditorCanvas.LoadImage(filePath);
            }

            if (loaded)
            {
                vm.ImageEditor.CurrentImagePath = filePath;
                
                // Sync layer state with ViewModel
                vm.ImageEditor.LayerPanel.IsLayerMode = _imageEditorCanvas.EditorCore.IsLayerMode;
                vm.ImageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
                
                vm.ImageEditor.StatusMessage = IsTiffFile(filePath)
                    ? $"Loaded: {Path.GetFileName(filePath)} ({_imageEditorCanvas.EditorCore.Layers?.Count ?? 1} layers)"
                    : $"Loaded: {Path.GetFileName(filePath)}";
                FileLogger.Log($"Successfully loaded dropped image: {filePath}");
            }
            else
            {
                vm.ImageEditor.StatusMessage = "Failed to load image.";
                FileLogger.LogError($"Failed to load dropped image: {filePath}");
            }
        }
        catch (Exception ex)
        {
            vm.ImageEditor.StatusMessage = $"Error loading image: {ex.Message}";
            FileLogger.LogError($"Exception loading dropped image: {filePath}", ex);
        }
    }

    private async void OnOpenImageButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ImageEditTabViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image Files")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.webp", "*.tiff", "*.tif"]
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        if (files.Count > 0)
        {
            var filePath = files[0].Path.LocalPath;
            LoadDroppedImage(filePath);
        }
    }

    #endregion
}
