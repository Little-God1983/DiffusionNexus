using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for comparing two images with a draggable slider.
/// </summary>
public partial class ImageComparerViewModel : ViewModelBase
{
    [ObservableProperty]
    private Bitmap? _imageBottom;

    [ObservableProperty]
    private Bitmap? _imageTop;

    [ObservableProperty]
    private double _sliderPosition = 0.5;

    [ObservableProperty]
    private bool _isDragOver;

    [ObservableProperty]
    private bool _hasImages;

    [ObservableProperty]
    private bool _hasBothImages;

    /// <summary>
    /// Programmatic entry point to load images by path.
    /// </summary>
    public void LoadImages(string? bottomPath, string? topPath)
    {
        var bottom = LoadBitmap(bottomPath);
        var top = LoadBitmap(topPath);

        if (bottom is not null)
        {
            ImageBottom = bottom;
        }

        if (top is not null)
        {
            ImageTop = top;
        }

        UpdateImageState();
    }

    /// <summary>
    /// Handles drag-and-drop file paths.
    /// </summary>
    public void HandleDroppedFiles(IEnumerable<string> filePaths)
    {
        var files = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        if (files.Count == 0)
        {
            return;
        }

        if (files.Count >= 2)
        {
            LoadImages(files[0], files[1]);
            return;
        }

        var file = files[0];
        var loaded = LoadBitmap(file);

        if (loaded is null)
        {
            return;
        }

        if (ImageTop is not null)
        {
            ImageBottom = ImageTop;
            ImageTop = loaded;
        }
        else if (ImageBottom is null)
        {
            ImageBottom = loaded;
        }
        else
        {
            ImageTop = loaded;
        }

        UpdateImageState();
    }

    partial void OnImageBottomChanged(Bitmap? value) => UpdateImageState();

    partial void OnImageTopChanged(Bitmap? value) => UpdateImageState();

    private void UpdateImageState()
    {
        HasImages = ImageBottom is not null || ImageTop is not null;
        HasBothImages = ImageBottom is not null && ImageTop is not null;
    }

    private static Bitmap? LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return new Bitmap(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
