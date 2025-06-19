using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.IO;
using System.Linq;

namespace DiffusionNexus.UI.Views
{
    public partial class PromptEditView : UserControl
    {
        private Border? _imageDropBorder;
        private TextBlock? _dropText;
        private Image? _previewImage;
        private readonly IBrush _defaultBorderBrush;

        public PromptEditView()
        {
            InitializeComponent();

            _imageDropBorder = this.FindControl<Border>("ImageDropBorder");
            _dropText = this.FindControl<TextBlock>("DropText");
            _previewImage = this.FindControl<Image>("PreviewImage");
            
            if (_imageDropBorder != null)
            {
                _defaultBorderBrush = _imageDropBorder.BorderBrush;
                
                // Set up drag drop handlers
                _imageDropBorder.AddHandler(DragDrop.DragEnterEvent, Border_DragEnter);
                _imageDropBorder.AddHandler(DragDrop.DragLeaveEvent, Border_DragLeave);
                _imageDropBorder.AddHandler(DragDrop.DragOverEvent, Border_DragOver);
                _imageDropBorder.AddHandler(DragDrop.DropEvent, Border_Drop);
            }

            if (_previewImage != null)
            {
                _previewImage.IsVisible = false;
            }
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void Border_DragEnter(object? sender, DragEventArgs e)
        {
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
            if (!e.Data.Contains(DataFormats.FileNames)) return;

            var files = e.Data.GetFileNames()?.ToList();
            if (files?.Count > 0)
            {
                var file = files[0];
                if (IsImagePath(file))
                {
                    try
                    {
                        if (_previewImage != null)
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
                    }
                    catch (Exception)
                    {
                        ResetDropArea();
                    }
                }
            }
        }

        private bool IsImageFile(DragEventArgs e)
        {
            if (!e.Data.Contains(DataFormats.FileNames)) return false;
            
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
            if (_imageDropBorder != null)
            {
                _imageDropBorder.BorderBrush = _defaultBorderBrush;
            }
            if (_dropText != null)
            {
                _dropText.Text = "Drop image here";
                _dropText.IsVisible = true;
            }
            if (_previewImage != null)
            {
                _previewImage.Source = null;
                _previewImage.IsVisible = false;
            }
        }
    }
}
