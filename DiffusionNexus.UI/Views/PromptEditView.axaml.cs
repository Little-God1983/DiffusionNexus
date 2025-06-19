using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System.Linq;

namespace DiffusionNexus.UI.Views
{
    public partial class PromptEditView : UserControl
    {
        private Border? _imageDropBorder;
        private TextBlock? _dropText;
        private readonly IBrush _defaultBorderBrush;

        public PromptEditView()
        {
            InitializeComponent();
            
            _imageDropBorder = this.FindControl<Border>("ImageDropBorder");
            _dropText = this.FindControl<TextBlock>("DropText");
            
            if (_imageDropBorder != null)
            {
                _defaultBorderBrush = _imageDropBorder.BorderBrush;
                _imageDropBorder.AddHandler(DragDrop.DragEnterEvent, DragEnter);
                _imageDropBorder.AddHandler(DragDrop.DragLeaveEvent, DragLeave);
                _imageDropBorder.AddHandler(DragDrop.DropEvent, Drop);
            }
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void DragEnter(object? sender, DragEventArgs e)
        {
            if (_imageDropBorder == null) return;

            if (e.Data.Contains(DataFormats.FileNames))
            {
                e.DragEffects = DragDropEffects.Copy;
                _imageDropBorder.BorderBrush = new SolidColorBrush(Colors.Green);
                if (_dropText != null)
                    _dropText.Text = "Release to drop";
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        }

        private void DragLeave(object? sender, DragEventArgs e)
        {
            if (_imageDropBorder == null) return;

            _imageDropBorder.BorderBrush = _defaultBorderBrush;
            if (_dropText != null)
                _dropText.Text = "Drop image here";
        }

        private void Drop(object? sender, DragEventArgs e)
        {
            if (_imageDropBorder == null) return;

            _imageDropBorder.BorderBrush = _defaultBorderBrush;
            if (_dropText != null)
                _dropText.Text = "Drop image here";

            if (e.Data.Contains(DataFormats.FileNames))
            {
                var files = e.Data.GetFileNames()?.ToList();
                if (files?.Count > 0)
                {
                    // TODO: Handle the dropped image file
                    var file = files[0];
                    if (_dropText != null)
                        _dropText.Text = System.IO.Path.GetFileName(file);
                }
            }
        }
    }
}
