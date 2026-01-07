using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Image Edit tab in the LoRA Dataset Helper.
/// Handles wiring up the ImageEditorControl events to the ViewModel.
/// </summary>
public partial class ImageEditView : UserControl
{
    private ImageEditorControl? _imageEditorCanvas;
    private bool _eventsWired;

    public ImageEditView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _imageEditorCanvas = this.FindControl<ImageEditorControl>("ImageEditorCanvas");
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_eventsWired) return;

        if (DataContext is ImageEditTabViewModel vm)
        {
            TryWireUpImageEditorEvents(vm);
        }
    }

    private void TryWireUpImageEditorEvents(ImageEditTabViewModel vm)
    {
        if (_eventsWired) return;

        _imageEditorCanvas ??= this.FindControl<ImageEditorControl>("ImageEditorCanvas");

        if (_imageEditorCanvas is not null)
        {
            WireUpImageEditorEvents(vm);
        }
        else
        {
            LayoutUpdated += OnLayoutUpdatedForEditorInit;
        }
    }

    private void OnLayoutUpdatedForEditorInit(object? sender, EventArgs e)
    {
        if (_eventsWired) return;

        _imageEditorCanvas ??= this.FindControl<ImageEditorControl>("ImageEditorCanvas");

        if (_imageEditorCanvas is not null && DataContext is ImageEditTabViewModel vm)
        {
            LayoutUpdated -= OnLayoutUpdatedForEditorInit;
            WireUpImageEditorEvents(vm);
        }
    }

    private void WireUpImageEditorEvents(ImageEditTabViewModel vm)
    {
        if (_imageEditorCanvas is null || _eventsWired) return;

        _eventsWired = true;
        var imageEditor = vm.ImageEditor;

        // Update dimensions and file info when image changes
        _imageEditorCanvas.ImageChanged += (_, _) =>
        {
            imageEditor.UpdateDimensions(
                _imageEditorCanvas.ImageWidth,
                _imageEditorCanvas.ImageHeight);
            imageEditor.UpdateFileInfo(
                _imageEditorCanvas.ImageDpi,
                _imageEditorCanvas.FileSizeBytes);
        };

        // Update zoom info when zoom changes
        _imageEditorCanvas.ZoomChanged += (_, _) =>
        {
            imageEditor.UpdateZoomInfo(
                _imageEditorCanvas.ZoomPercentage,
                _imageEditorCanvas.IsFitMode);
        };

        // Handle crop applied
        _imageEditorCanvas.CropApplied += (_, _) =>
        {
            imageEditor.OnCropApplied();
        };

        // Handle clear/reset requests from ViewModel
        imageEditor.ClearRequested += (_, _) =>
        {
            _imageEditorCanvas.ClearImage();
        };

        imageEditor.ResetRequested += (_, _) =>
        {
            _imageEditorCanvas.ResetToOriginal();
        };

        // Handle crop tool activation/deactivation
        imageEditor.CropToolActivated += (_, _) =>
        {
            _imageEditorCanvas.ActivateCropTool();
        };

        imageEditor.CropToolDeactivated += (_, _) =>
        {
            _imageEditorCanvas.DeactivateCropTool();
        };

        // Handle crop apply/cancel requests
        imageEditor.ApplyCropRequested += (_, _) =>
        {
            if (_imageEditorCanvas.ApplyCrop())
            {
                imageEditor.OnCropApplied();
            }
        };

        imageEditor.CancelCropRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.CropTool.ClearCropRegion();
            _imageEditorCanvas.DeactivateCropTool();
        };

        // Handle zoom requests
        imageEditor.ZoomInRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomIn();
        };

        imageEditor.ZoomOutRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomOut();
        };

        imageEditor.ZoomToFitRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomToFit();
        };

        imageEditor.ZoomToActualRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomToActual();
        };

        // Handle transform requests
        imageEditor.RotateLeftRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.RotateLeft();
        };

        imageEditor.RotateRightRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.RotateRight();
        };

        imageEditor.Rotate180Requested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.Rotate180();
        };

        imageEditor.FlipHorizontalRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.FlipHorizontal();
        };

        imageEditor.FlipVerticalRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.FlipVertical();
        };

        // Handle save requests
        imageEditor.SaveAsNewRequested += (_, _) =>
        {
            var newPath = _imageEditorCanvas.EditorCore.SaveAsNew();
            if (newPath is not null)
            {
                imageEditor.OnSaveAsNewCompleted(newPath);
            }
            else
            {
                imageEditor.StatusMessage = "Failed to save image.";
            }
        };

        imageEditor.SaveOverwriteConfirmRequested += async () =>
        {
            if (vm.DialogService is not null)
            {
                return await vm.DialogService.ShowConfirmAsync(
                    "Overwrite Image",
                    "Do you really want to overwrite your original image? This cannot be undone.");
            }
            return false;
        };

        imageEditor.SaveOverwriteRequested += (_, _) =>
        {
            if (_imageEditorCanvas.EditorCore.SaveOverwrite())
            {
                imageEditor.OnSaveOverwriteCompleted();
            }
            else
            {
                imageEditor.StatusMessage = "Failed to save image.";
            }
        };

        // Wire up zoom slider
        var zoomSlider = this.FindControl<Slider>("ZoomSlider");
        if (zoomSlider is not null)
        {
            zoomSlider.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name == nameof(Slider.Value))
                {
                    var percentage = (int)zoomSlider.Value;
                    _imageEditorCanvas.SetZoom(percentage / 100f);
                }
            };
        }
    }
}
