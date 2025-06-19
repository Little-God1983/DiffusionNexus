using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.IO;
using System.Linq;
using System.Text;
using DiffusionNexus.UI.Classes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Png.Chunks;

namespace DiffusionNexus.UI.Views
{
    public partial class PromptEditView : UserControl
    {
        private Border? _imageDropBorder;
        private TextBlock? _dropText;
        private Avalonia.Controls.Image? _previewImage;
        private TextBox? _promptBox;
        private TextBox? _negativePromptBox;
        private TextBox? _blacklistBox;
        private string? _currentImagePath;
        private StableDiffusionMetadata? _metadata;
        private IBrush _defaultBorderBrush = Brushes.Transparent;

        public PromptEditView()
        {
            InitializeComponent();

            _imageDropBorder = this.FindControl<Border>("ImageDropBorder");
            _dropText = this.FindControl<TextBlock>("DropText");
            _previewImage = this.FindControl<Avalonia.Controls.Image>("PreviewImage");
            _promptBox = this.FindControl<TextBox>("PromptBox");
            _negativePromptBox = this.FindControl<TextBox>("NegativePromptBox");
            _blacklistBox = this.FindControl<TextBox>("BlacklistBox");

            if (_imageDropBorder != null)
            {
                _defaultBorderBrush = _imageDropBorder.BorderBrush;

                // Set up drag drop handlers
                _imageDropBorder.AddHandler(DragDrop.DragEnterEvent, Border_DragEnter);
                _imageDropBorder.AddHandler(DragDrop.DragLeaveEvent, Border_DragLeave);
                _imageDropBorder.AddHandler(DragDrop.DragOverEvent, Border_DragOver);
                _imageDropBorder.AddHandler(DragDrop.DropEvent, Border_Drop);
            }

            if (_previewImage is not null)
            {
                _previewImage.IsVisible = false;
            }
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void Border_DragEnter(object? sender, DragEventArgs e)
        {
            var files = e.Data.GetFileNames()?.ToList();

            if (!IsImageFile(e)) return;

            if (_imageDropBorder != null)
            {
                _imageDropBorder.BorderBrush = new SolidColorBrush(Colors.Green);
            }
            if (_dropText != null)
            {
                _dropText.Text = "Release to drop";
            }
        }

        private void Border_DragLeave(object? sender, DragEventArgs e)
        {
            ResetDropArea();
        }

        private void Border_DragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = IsImageFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void Border_Drop(object? sender, DragEventArgs e)
        {
            if (!IsImageFile(e)) return;

            var files = e.Data.GetFileNames()?.ToList();
            if (files?.Count > 0)
            {
                var file = files[0];
                if (IsImagePath(file))
                {
                    try
                    {
                        if (_previewImage is not null)
                        {
                            using var stream = File.OpenRead(file);
                            var bitmap = new Bitmap(stream);
                            _previewImage.Source = bitmap;
                            _previewImage.IsVisible = true;
                        }
                        if (_dropText != null)
                        {
                            _dropText.IsVisible = false;
                        }

                        // extract metadata and populate prompt boxes
                        var meta = PngMetadataReader.ReadMetadata(file);
                        if (meta != null)
                        {
                            if (_promptBox != null)
                                _promptBox.Text = meta.Prompt ?? string.Empty;
                            if (_negativePromptBox != null)
                                _negativePromptBox.Text = meta.NegativePrompt ?? string.Empty;
                            _metadata = meta;
                        }
                        else
                        {
                            _metadata = null;
                        }

                        _currentImagePath = file;
                    }
                    catch (Exception)
                    {
                        ResetDropArea();
                    }
                    finally
                    {
                        ResetBorderBrush();
                    }
                }
            }
        }

        private bool IsImageFile(DragEventArgs e)
        {
            var files = e.Data.GetFileNames()?.ToList();
            return files?.Count > 0 && IsImagePath(files[0]);
        }

        private bool IsImagePath(string path)
        {
            var extension = Path.GetExtension(path).ToLower();
            return new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp" }.Contains(extension);
        }

        private void ResetDropArea()
        {
            ResetBorderBrush();
            if (_dropText != null)
            {
                _dropText.Text = "Drop image here";
                _dropText.IsVisible = true;
            }
            ClearImageArea();
        }

        private void ResetBorderBrush()
        {
            if (_imageDropBorder != null)
            {
                _imageDropBorder.BorderBrush = _defaultBorderBrush;
            }
        }

        private void ClearImageArea()
        {
            if (_previewImage is not null)
            {
                _previewImage.Source = null;
                _previewImage.IsVisible = false;
            }
        }

        private string BuildParametersString()
        {
            var prompt = _promptBox?.Text ?? string.Empty;
            var negPrompt = _negativePromptBox?.Text ?? string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine($"Prompt: {prompt}");
            sb.AppendLine($"Negative prompt: {negPrompt}");

            if (_metadata != null)
            {
                var parts = new System.Collections.Generic.List<string>();
                if (_metadata.Steps > 0) parts.Add($"Steps: {_metadata.Steps}");
                if (!string.IsNullOrWhiteSpace(_metadata.Sampler)) parts.Add($"Sampler: {_metadata.Sampler}");
                if (_metadata.CFGScale != 0) parts.Add($"CFG scale: {_metadata.CFGScale}");
                if (_metadata.Seed != 0) parts.Add($"Seed: {_metadata.Seed}");
                if (_metadata.Width > 0 && _metadata.Height > 0) parts.Add($"Size: {_metadata.Width}x{_metadata.Height}");
                if (!string.IsNullOrWhiteSpace(_metadata.ModelHash)) parts.Add($"Model hash: {_metadata.ModelHash}");
                if (parts.Count > 0)
                    sb.AppendLine(string.Join(", ", parts));
            }

            return sb.ToString();
        }

        private void SaveImage(string path)
        {
            if (string.IsNullOrEmpty(_currentImagePath))
                return;

            using (var image = SixLabors.ImageSharp.Image.Load(_currentImagePath))
            {
                var pngMeta = image.Metadata.GetPngMetadata();
                var parameters = BuildParametersString();
                // Remove old entry if it exists
                var old = pngMeta.TextData.FirstOrDefault(t => t.Keyword == "parameters");
                if (old != null)
                    pngMeta.TextData.Remove(old);
                // Add new entry as PngTextData
                pngMeta.TextData.Add(new PngTextData("parameters", parameters, null, null));
                image.Save(path);
            }
        }

        private void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentImagePath))
                return;

            SaveImage(_currentImagePath);
        }

        private async void OnSaveAs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentImagePath))
                return;

            var dialog = new SaveFileDialog
            {
                Filters = { new FileDialogFilter { Name = "PNG", Extensions = { "png" } } },
                InitialFileName = Path.GetFileName(_currentImagePath)
            };

            if (this.VisualRoot is not Window window)
                return;

            var result = await dialog.ShowAsync(window);
            if (!string.IsNullOrEmpty(result))
            {
                SaveImage(result);
            }
        }

        private void OnApplyBlacklist(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_blacklistBox == null)
                return;

            var words = _blacklistBox.Text?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
            if (_promptBox != null)
                _promptBox.Text = ApplyBlacklist(_promptBox.Text, words);
            if (_negativePromptBox != null)
                _negativePromptBox.Text = ApplyBlacklist(_negativePromptBox.Text, words);
        }

        private static string ApplyBlacklist(string text, string[] words)
        {
            var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .Select(p => p.Trim());
            var filtered = parts.Where(p => !words.Contains(p, StringComparer.OrdinalIgnoreCase));
            return string.Join(", ", filtered);
        }
    }
}
