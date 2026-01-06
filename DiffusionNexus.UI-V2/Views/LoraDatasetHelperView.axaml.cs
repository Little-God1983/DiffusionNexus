using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// View for the LoRA Dataset Helper module.
/// </summary>
public partial class LoraDatasetHelperView : UserControl
{
    private ImageEditorControl? _imageEditorCanvas;

    public LoraDatasetHelperView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _imageEditorCanvas = this.FindControl<ImageEditorControl>("ImageEditorCanvas");
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Inject DialogService into the ViewModel
        if (VisualRoot is Window window && DataContext is IDialogServiceAware aware)
        {
            aware.DialogService = new DialogService(window);
        }

        // Load datasets on first attach
        if (DataContext is LoraDatasetHelperViewModel vm)
        {
            vm.CheckStorageConfigurationCommand.Execute(null);

            // Wire up image editor events
            WireUpImageEditorEvents(vm);
        }
    }

    private void WireUpImageEditorEvents(LoraDatasetHelperViewModel vm)
    {
        if (_imageEditorCanvas is null) return;

        // Update dimensions when image changes
        _imageEditorCanvas.ImageChanged += (_, _) =>
        {
            vm.ImageEditor.UpdateDimensions(
                _imageEditorCanvas.ImageWidth,
                _imageEditorCanvas.ImageHeight);
        };

        // Handle crop applied
        _imageEditorCanvas.CropApplied += (_, _) =>
        {
            vm.ImageEditor.OnCropApplied();
        };

        // Handle clear/reset requests from ViewModel
        vm.ImageEditor.ClearRequested += (_, _) =>
        {
            _imageEditorCanvas.ClearImage();
        };

        vm.ImageEditor.ResetRequested += (_, _) =>
        {
            _imageEditorCanvas.ResetToOriginal();
        };

        // Handle crop tool activation/deactivation
        vm.ImageEditor.CropToolActivated += (_, _) =>
        {
            _imageEditorCanvas.ActivateCropTool();
        };

        vm.ImageEditor.CropToolDeactivated += (_, _) =>
        {
            _imageEditorCanvas.DeactivateCropTool();
        };

        // Handle crop apply/cancel requests
        vm.ImageEditor.ApplyCropRequested += (_, _) =>
        {
            if (_imageEditorCanvas.ApplyCrop())
            {
                vm.ImageEditor.OnCropApplied();
            }
        };

        vm.ImageEditor.CancelCropRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.CropTool.ClearCropRegion();
            _imageEditorCanvas.DeactivateCropTool();
        };

        // Handle save requests
        vm.ImageEditor.SaveAsNewRequested += async (_, _) =>
        {
            var newPath = _imageEditorCanvas.EditorCore.SaveAsNew();
            if (newPath is not null)
            {
                vm.ImageEditor.OnSaveAsNewCompleted(newPath);
                await vm.RefreshActiveDatasetAsync();
            }
            else
            {
                vm.ImageEditor.StatusMessage = "Failed to save image.";
            }
        };

        vm.ImageEditor.SaveOverwriteConfirmRequested += async () =>
        {
            if (vm.DialogService is not null)
            {
                return await vm.DialogService.ShowConfirmAsync(
                    "Overwrite Image",
                    "Do you really want to overwrite your original image? This cannot be undone.");
            }
            return false;
        };

        vm.ImageEditor.SaveOverwriteRequested += async (_, _) =>
        {
            if (_imageEditorCanvas.EditorCore.SaveOverwrite())
            {
                vm.ImageEditor.OnSaveOverwriteCompleted();
                await vm.RefreshActiveDatasetAsync();
            }
            else
            {
                vm.ImageEditor.StatusMessage = "Failed to save image.";
            }
        };
    }
}
