using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Views
{
    public partial class PromptEditView : UserControl
    {
        private Border? _imageDropBorder;
        private TextBlock? _dropText;
        private Avalonia.Controls.Image? _previewImage;
        private PromptEditorControl? _singlePromptEditor;
        // Text boxes bound via XAML
        private TextBox? _stepsBox;
        private TextBox? _samplerBox;
        private TextBox? _scheduleTypeBox;
        private TextBox? _cfgScaleBox;
        private TextBox? _seedBox;
        private TextBox? _faceRestorationBox;
        private TextBox? _sizeBox;
        private TextBox? _modelHashBox;
        private TextBox? _modelBox;
        private TextBox? _tiBox;
        private TextBox? _versionBox;
        private TextBox? _sourceIdentifierBox;
        private TextBox? _loraHashesBox;
        private TextBox? _widthBox;
        private TextBox? _heightBox;
        private TextBox? _hashesBox;
        private TextBox? _resourcesBox;
        private Button? _copyMetadataButton;
        private Button? _saveProfileButton;
        private Button? _deleteProfileButton;
        private string? _currentImagePath;
        private StableDiffusionMetadata? _metadata;
        private IBrush _defaultBorderBrush = Brushes.Transparent;

        public PromptEditView()
        {
            InitializeComponent();

            _imageDropBorder = this.FindControl<Border>("ImageDropBorder");
            _dropText = this.FindControl<TextBlock>("DropText");
            _previewImage = this.FindControl<Avalonia.Controls.Image>("PreviewImage");
            _singlePromptEditor = this.FindControl<PromptEditorControl>("SinglePromptEditor");

            _stepsBox = this.FindControl<TextBox>("StepsBox");
            _samplerBox = this.FindControl<TextBox>("SamplerBox");
            _scheduleTypeBox = this.FindControl<TextBox>("ScheduleTypeBox");
            _cfgScaleBox = this.FindControl<TextBox>("CFGScaleBox");
            _seedBox = this.FindControl<TextBox>("SeedBox");
            _faceRestorationBox = this.FindControl<TextBox>("FaceRestorationBox");
            _sizeBox = this.FindControl<TextBox>("SizeBox");
            _modelHashBox = this.FindControl<TextBox>("ModelHashBox");
            _modelBox = this.FindControl<TextBox>("ModelBox");
            _tiBox = this.FindControl<TextBox>("TIBox");
            _versionBox = this.FindControl<TextBox>("VersionBox");
            _sourceIdentifierBox = this.FindControl<TextBox>("SourceIdentifierBox");
            _loraHashesBox = this.FindControl<TextBox>("LoRAHashesBox");
            _widthBox = this.FindControl<TextBox>("WidthBox");
            _heightBox = this.FindControl<TextBox>("HeightBox");
            _hashesBox = this.FindControl<TextBox>("HashesBox");
            _resourcesBox = this.FindControl<TextBox>("ResourcesBox");
            _copyMetadataButton = this.FindControl<Button>("CopyMetadataButton");
            _saveProfileButton = this.FindControl<Button>("SaveProfileButton");
            _deleteProfileButton = this.FindControl<Button>("DeleteProfileButton");

            if (_copyMetadataButton is not null)
                _copyMetadataButton.Click += OnCopyMetadata;

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
                            if (_singlePromptEditor?.DataContext is PromptEditorControlViewModel vm)
                            {
                                vm.Prompt = meta.Prompt ?? string.Empty;
                                vm.NegativePrompt = meta.NegativePrompt ?? string.Empty;
                                _metadata = meta;
                                DisplayMetadata();
                            }
                        }
                        else
                        {
                            _metadata = null;
                            ClearMetadata();
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
            ClearMetadata();
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

        private void DisplayMetadata()
        {
            if (_metadata == null)
                return;

            if (_stepsBox != null) _stepsBox.Text = _metadata.Steps.ToString();
            if (_samplerBox != null) _samplerBox.Text = _metadata.Sampler ?? string.Empty;
            if (_scheduleTypeBox != null) _scheduleTypeBox.Text = _metadata.ScheduleType ?? string.Empty;
            if (_cfgScaleBox != null) _cfgScaleBox.Text = _metadata.CFGScale.ToString();
            if (_seedBox != null) _seedBox.Text = _metadata.Seed.ToString();
            if (_faceRestorationBox != null) _faceRestorationBox.Text = _metadata.FaceRestoration ?? string.Empty;
            if (_sizeBox != null) _sizeBox.Text = _metadata.Width > 0 && _metadata.Height > 0 ? $"{_metadata.Width}x{_metadata.Height}" : string.Empty;
            if (_modelHashBox != null) _modelHashBox.Text = _metadata.ModelHash ?? string.Empty;
            if (_modelBox != null) _modelBox.Text = _metadata.Model ?? string.Empty;
            if (_tiBox != null) _tiBox.Text = _metadata.TI ?? string.Empty;
            if (_versionBox != null) _versionBox.Text = _metadata.Version ?? string.Empty;
            if (_sourceIdentifierBox != null) _sourceIdentifierBox.Text = _metadata.SourceIdentifier ?? string.Empty;
            if (_loraHashesBox != null) _loraHashesBox.Text = _metadata.LoRAHashes ?? string.Empty;
            if (_widthBox != null) _widthBox.Text = _metadata.Width.ToString();
            if (_heightBox != null) _heightBox.Text = _metadata.Height.ToString();
            if (_hashesBox != null) _hashesBox.Text = _metadata.Hashes ?? string.Empty;
            if (_resourcesBox != null) _resourcesBox.Text = _metadata.Resources ?? string.Empty;
        }

        private void ClearMetadata()
        {
            var boxes = new TextBox?[] { _stepsBox, _samplerBox, _scheduleTypeBox, _cfgScaleBox, _seedBox, _faceRestorationBox, _sizeBox, _modelHashBox, _modelBox, _tiBox, _versionBox, _sourceIdentifierBox, _loraHashesBox, _widthBox, _heightBox, _hashesBox, _resourcesBox };
            foreach (var box in boxes)
            {
                if (box != null) box.Text = string.Empty;
            }
        }

        private string BuildParametersString()
        {
            var sb = new StringBuilder();
            if (_singlePromptEditor?.DataContext is PromptEditorControlViewModel vm)
            {
                var prompt = vm.Prompt ?? string.Empty;
                var negPrompt = vm.NegativePrompt ?? string.Empty;

                sb.AppendLine($"Prompt: {prompt}");
                sb.AppendLine($"Negative prompt: {negPrompt}");
            }
            if (_metadata != null)
            {
                var parts = new System.Collections.Generic.List<string>();
                if (_metadata.Steps > 0) parts.Add($"Steps: {_metadata.Steps}");
                if (!string.IsNullOrWhiteSpace(_metadata.Sampler)) parts.Add($"Sampler: {_metadata.Sampler}");
                if (!string.IsNullOrWhiteSpace(_metadata.ScheduleType)) parts.Add($"Schedule type: {_metadata.ScheduleType}");
                if (_metadata.CFGScale != 0) parts.Add($"CFG scale: {_metadata.CFGScale}");
                if (_metadata.Seed != 0) parts.Add($"Seed: {_metadata.Seed}");
                if (!string.IsNullOrWhiteSpace(_metadata.FaceRestoration)) parts.Add($"Face restoration: {_metadata.FaceRestoration}");
                if (_metadata.Width > 0 && _metadata.Height > 0) parts.Add($"Size: {_metadata.Width}x{_metadata.Height}");
                if (!string.IsNullOrWhiteSpace(_metadata.ModelHash)) parts.Add($"Model hash: {_metadata.ModelHash}");
                if (!string.IsNullOrWhiteSpace(_metadata.Model)) parts.Add($"Model: {_metadata.Model}");
                if (!string.IsNullOrWhiteSpace(_metadata.TI)) parts.Add($"TI: {_metadata.TI}");
                if (!string.IsNullOrWhiteSpace(_metadata.Version)) parts.Add($"Version: {_metadata.Version}");
                if (!string.IsNullOrWhiteSpace(_metadata.SourceIdentifier)) parts.Add($"Source Identifier: {_metadata.SourceIdentifier}");
                if (!string.IsNullOrWhiteSpace(_metadata.LoRAHashes)) parts.Add($"LoRA hashes: {_metadata.LoRAHashes}");
                if (_metadata.Width > 0) parts.Add($"Width: {_metadata.Width}");
                if (_metadata.Height > 0) parts.Add($"Height: {_metadata.Height}");
                if (!string.IsNullOrWhiteSpace(_metadata.Hashes)) parts.Add($"Hashes: {_metadata.Hashes}");
                if (!string.IsNullOrWhiteSpace(_metadata.Resources)) parts.Add($"Resources: {_metadata.Resources}");
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

        private async void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentImagePath))
                return;

            if (File.Exists(_currentImagePath) && this.VisualRoot is Window window)
            {
                var confirm = await ConfirmOverwriteAsync(window);
                if (!confirm)
                    return;
            }

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

        private async Task<bool> ConfirmOverwriteAsync(Window owner)
        {
            var tcs = new TaskCompletionSource<bool>();

            var dialog = new Window
            {
                Width = 300,
                Height = 150,
                Title = "Confirm Save",
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var yesButton = new Button { Content = "Yes", Width = 80 };
            var noButton = new Button { Content = "No", Width = 80 };

            yesButton.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
            noButton.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };

            dialog.Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(10),
                Children =
                {
                    new TextBlock { Text = "Overwrite the existing file?", TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Spacing = 10,
                        Children = { yesButton, noButton }
                    }
                }
            };

            await dialog.ShowDialog(owner);
            return await tcs.Task;
        }

        private void OnApplyBlacklist(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var vm = DataContext as PromptEditorControlViewModel;
            var words = vm?.Blacklist?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => w.ToLowerInvariant())
                .ToArray() ?? Array.Empty<string>();

            if (vm.Prompt != null)
            {
                var prompt = ApplyBlacklist(vm.Prompt, words);
                var whitelist = vm.Whitelist ?? vm?.Whitelist ?? string.Empty;
                vm.Prompt = AppendWhitelist(prompt, whitelist);
            }

            if (vm.NegativePrompt != null)
            {
                vm.NegativePrompt = ApplyBlacklist(vm.NegativePrompt, words);
            }
        }

        private static string ApplyBlacklist(string text, string[] words)
        {
            if (string.IsNullOrWhiteSpace(text) || words.Length == 0)
                return text;

            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word))
                    continue;

                var pattern = $"\\b{System.Text.RegularExpressions.Regex.Escape(word)}\\b";
                text = System.Text.RegularExpressions.Regex.Replace(text, pattern, string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            // Normalize commas and spaces
            text = Regex.Replace(text, @"\s+,", ",");
            text = Regex.Replace(text, @",\s*,", ",");
            text = Regex.Replace(text, @"\s{2,}", " ");
            text = Regex.Replace(text, @"\s*,\s*", ", ");


            return text.Trim(' ', ',');
        }

        private static string AppendWhitelist(string text, string whitelist)
        {
            if (string.IsNullOrWhiteSpace(whitelist))
                return text.Trim(' ', ',');

            text = text.Trim(' ', ',');
            var trimmedWhitelist = whitelist.Trim();

            if (text.EndsWith(trimmedWhitelist, StringComparison.OrdinalIgnoreCase))
                return text;

            if (string.IsNullOrWhiteSpace(text))
                return trimmedWhitelist;

            if (!text.EndsWith(","))
                text += ",";

            return $"{text} {trimmedWhitelist}";
        }

        private async void OnCopyMetadata(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_metadata == null)
                return;

            var meta = new
            {
                _metadata.Steps,
                _metadata.Sampler,
                ScheduleType = _metadata.ScheduleType,
                CFGScale = _metadata.CFGScale,
                _metadata.Seed,
                FaceRestoration = _metadata.FaceRestoration,
                Size = _metadata.Width > 0 && _metadata.Height > 0 ? $"{_metadata.Width}x{_metadata.Height}" : null,
                _metadata.ModelHash,
                _metadata.Model,
                _metadata.TI,
                _metadata.Version,
                SourceIdentifier = _metadata.SourceIdentifier,
                LoRAHashes = _metadata.LoRAHashes,
                _metadata.Width,
                _metadata.Height,
                _metadata.Hashes,
                _metadata.Resources
            };

            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
                await topLevel.Clipboard.SetTextAsync(json);
        }

    }
}
