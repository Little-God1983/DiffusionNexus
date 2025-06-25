using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Classes;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Media;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class PromptEditViewModel : ViewModelBase
    {
        public PromptEditorControlViewModel SinglePromptVm { get; } = new();
        public PromptEditorControlViewModel BatchPromptVm { get; } = new();
        public BatchProcessingViewModel BatchViewModel { get; } = new();

        [ObservableProperty]
        private Bitmap? previewImage;

        [ObservableProperty]
        private bool isPreviewVisible;

        [ObservableProperty]
        private string dropText = "Drop image here";

        [ObservableProperty] private string? steps;
        [ObservableProperty] private string? sampler;
        [ObservableProperty] private string? scheduleType;
        [ObservableProperty] private string? cfgScale;
        [ObservableProperty] private string? seed;
        [ObservableProperty] private string? faceRestoration;
        [ObservableProperty] private string? size;
        [ObservableProperty] private string? modelHash;
        [ObservableProperty] private string? model;
        [ObservableProperty] private string? ti;
        [ObservableProperty] private string? version;
        [ObservableProperty] private string? sourceIdentifier;
        [ObservableProperty] private string? loRAHashes;
        [ObservableProperty] private string? width;
        [ObservableProperty] private string? height;
        [ObservableProperty] private string? hashes;
        [ObservableProperty] private string? resources;

        private string? _currentImagePath;
        private StableDiffusionMetadata? _metadata;

        public IRelayCommand ApplyBlacklistCommand { get; }
        public IAsyncRelayCommand<Window?> SaveCommand { get; }
        public IAsyncRelayCommand<Window?> SaveAsCommand { get; }
        public IAsyncRelayCommand<Window?> CopyMetadataCommand { get; }

        public PromptEditViewModel()
        {
            ApplyBlacklistCommand = new RelayCommand(OnApplyBlacklist);
            SaveCommand = new AsyncRelayCommand<Window?>(OnSaveAsync);
            SaveAsCommand = new AsyncRelayCommand<Window?>(OnSaveAsAsync);
            CopyMetadataCommand = new AsyncRelayCommand<Window?>(OnCopyMetadataAsync);
        }

        public void OnDragEnter(DragEventArgs e)
        {
            if (IsImageFile(e))
                DropText = "Release to drop";
        }

        public void OnDragLeave(DragEventArgs e)
        {
            ResetDropArea();
        }

        public void OnDragOver(DragEventArgs e)
        {
            e.DragEffects = IsImageFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        public void OnDrop(DragEventArgs e)
        {
            if (!IsImageFile(e)) return;
            var file = e.Data.GetFileNames()?.FirstOrDefault();
            if (file == null) return;
            try
            {
                using var stream = File.OpenRead(file);
                PreviewImage = new Bitmap(stream);
                IsPreviewVisible = true;
                DropText = string.Empty;
                var meta = PngMetadataReader.ReadMetadata(file);
                if (meta != null)
                {
                    SinglePromptVm.Prompt = meta.Prompt ?? string.Empty;
                    SinglePromptVm.NegativePrompt = meta.NegativePrompt ?? string.Empty;
                    _metadata = meta;
                    DisplayMetadata();
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
        }

        private bool IsImageFile(DragEventArgs e)
        {
            var files = e.Data.GetFileNames()?.ToList();
            return files?.Count > 0 && IsImagePath(files[0]);
        }

        private static bool IsImagePath(string path)
        {
            var extension = Path.GetExtension(path).ToLower();
            return new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp" }.Contains(extension);
        }

        private void ResetDropArea()
        {
            DropText = "Drop image here";
            PreviewImage = null;
            IsPreviewVisible = false;
            ClearMetadata();
        }

        private void DisplayMetadata()
        {
            if (_metadata == null) return;
            Steps = _metadata.Steps.ToString();
            Sampler = _metadata.Sampler;
            ScheduleType = _metadata.ScheduleType;
            CfgScale = _metadata.CFGScale.ToString();
            Seed = _metadata.Seed.ToString();
            FaceRestoration = _metadata.FaceRestoration;
            Size = _metadata.Width > 0 && _metadata.Height > 0 ? $"{_metadata.Width}x{_metadata.Height}" : string.Empty;
            ModelHash = _metadata.ModelHash;
            Model = _metadata.Model;
            Ti = _metadata.TI;
            Version = _metadata.Version;
            SourceIdentifier = _metadata.SourceIdentifier;
            LoRAHashes = _metadata.LoRAHashes;
            Width = _metadata.Width.ToString();
            Height = _metadata.Height.ToString();
            Hashes = _metadata.Hashes;
            Resources = _metadata.Resources;
        }

        private void ClearMetadata()
        {
            Steps = Sampler = ScheduleType = CfgScale = Seed = FaceRestoration = Size = null;
            ModelHash = Model = Ti = Version = SourceIdentifier = LoRAHashes = null;
            Width = Height = Hashes = Resources = null;
        }

        private string BuildParametersString()
        {
            var sb = new StringBuilder();
            var prompt = SinglePromptVm.Prompt ?? string.Empty;
            var negPrompt = SinglePromptVm.NegativePrompt ?? string.Empty;
            sb.AppendLine($"Prompt: {prompt}");
            sb.AppendLine($"Negative prompt: {negPrompt}");
            if (_metadata != null)
            {
                var parts = new List<string>();
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
            using var image = SixLabors.ImageSharp.Image.Load(_currentImagePath);
            var pngMeta = image.Metadata.GetPngMetadata();
            var parameters = BuildParametersString();
            var old = pngMeta.TextData.FirstOrDefault(t => t.Keyword == "parameters");
            if (old != null) pngMeta.TextData.Remove(old);
            pngMeta.TextData.Add(new PngTextData("parameters", parameters, null, null));
            image.Save(path);
        }

        private async Task OnSaveAsync(Window? window)
        {
            if (string.IsNullOrEmpty(_currentImagePath))
                return;
            if (File.Exists(_currentImagePath) && window != null)
            {
                var confirm = await ConfirmOverwriteAsync(window);
                if (!confirm) return;
            }
            SaveImage(_currentImagePath);
        }

        private async Task OnSaveAsAsync(Window? window)
        {
            if (string.IsNullOrEmpty(_currentImagePath) || window == null)
                return;
            var dialog = new SaveFileDialog
            {
                Filters = { new FileDialogFilter { Name = "PNG", Extensions = { "png" } } },
                InitialFileName = Path.GetFileName(_currentImagePath)
            };
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

        private void OnApplyBlacklist()
        {
            var words = SinglePromptVm.Blacklist?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => w.ToLowerInvariant())
                .ToArray() ?? Array.Empty<string>();
            if (SinglePromptVm.Prompt != null)
            {
                var prompt = ApplyBlacklist(SinglePromptVm.Prompt, words);
                var whitelist = SinglePromptVm.Whitelist ?? string.Empty;
                SinglePromptVm.Prompt = AppendWhitelist(prompt, whitelist);
            }
            if (SinglePromptVm.NegativePrompt != null)
            {
                SinglePromptVm.NegativePrompt = ApplyBlacklist(SinglePromptVm.NegativePrompt, words);
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
                var pattern = $"\\b{Regex.Escape(word)}\\b";
                text = Regex.Replace(text, pattern, string.Empty, RegexOptions.IgnoreCase);
            }
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

        private async Task OnCopyMetadataAsync(Window? window)
        {
            if (_metadata == null || window == null)
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
            await window.Clipboard.SetTextAsync(json);
        }
    }
}
