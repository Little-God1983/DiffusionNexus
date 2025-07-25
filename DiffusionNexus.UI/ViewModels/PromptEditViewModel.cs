using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Classes;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.UI.ViewModels
{
    public partial class PromptEditViewModel : ViewModelBase
    {
        public IDialogService DialogService { get; set; } = null!;
        public PromptEditorControlViewModel SinglePromptVm { get; } = new();
        public PromptEditorControlViewModel BatchPromptVm { get; } = new();
        public BatchProcessingViewModel BatchViewModel { get; } = new();

        [ObservableProperty]
        private Bitmap? previewImage;

        [ObservableProperty]
        private bool isPreviewVisible;

        [ObservableProperty]
        private string dropText = "Drop image here";

        [ObservableProperty]
        private Brush borderBrush = new SolidColorBrush(Colors.Gray);

        [ObservableProperty]
        private bool isDragOver;

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
        public IAsyncRelayCommand SaveCommand { get; }
        public IAsyncRelayCommand SaveAsCommand { get; }
        public IAsyncRelayCommand CopyMetadataCommand { get; }

        private Window? _window;

        public PromptEditViewModel()
        {
            ApplyBlacklistCommand = new RelayCommand(OnApplyBlacklist);
            SaveCommand = new AsyncRelayCommand(OnSaveAsync);
            SaveAsCommand = new AsyncRelayCommand(OnSaveAsAsync);
            CopyMetadataCommand = new AsyncRelayCommand(OnCopyMetadataAsync);
        }

        public void SetWindow(Window window)
        {
            _window = window;
        }

        public void OnDragEnter(DragEventArgs e)
        {
            IsDragOver = true;
            if (IsImageFile(e))
            {
                DropText = "Release to drop";
                BorderBrush = new SolidColorBrush(Colors.Green);
                Log($"Drag enter with valid image file - setting border to green", LogSeverity.Info);
            }
            else
            {
                DropText = "Unsupported file type";
                BorderBrush = new SolidColorBrush(Colors.Red);
                Log($"Drag enter with invalid file - setting border to red", LogSeverity.Warning);
            }
        }

        public void OnDragLeave(DragEventArgs e)
        {
            IsDragOver = false;
            Log($"Drag leave - resetting border", LogSeverity.Info);
            ResetDropArea();
        }

        public void OnDragOver(DragEventArgs e)
        {
            e.DragEffects = IsImageFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        public void OnDrop(DragEventArgs e)
        {
            IsDragOver = false;
            if (!IsImageFile(e)) 
            {
                ResetDropArea();
                return;
            }
            
            var storageFile = e.Data.GetFiles()?.FirstOrDefault();
            var file = storageFile?.TryGetLocalPath();
            if (string.IsNullOrEmpty(file)) 
            {
                ResetDropArea();
                return;
            }
            
            try
            {
                using var stream = File.OpenRead(file);
                PreviewImage = new Bitmap(stream);
                IsPreviewVisible = true;
                DropText = string.Empty;
                BorderBrush = new SolidColorBrush(Colors.Gray); // Reset to default
                
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
            var items = e.Data.GetFiles()?.ToList();
            var path = items?.FirstOrDefault()?.TryGetLocalPath();
            return !string.IsNullOrEmpty(path) && IsImagePath(path!);
        }

        private static bool IsImagePath(string path)
        {
            var extension = Path.GetExtension(path).ToLower();
            return new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp" }.Contains(extension);
        }

        private void ResetDropArea()
        {
            DropText = "Drop image here";
            BorderBrush = new SolidColorBrush(Colors.Gray);
            if (!IsPreviewVisible)
            {
                PreviewImage = null;
                ClearMetadata();
            }
        }

        private void DisplayMetadata()
        {
            if (_metadata == null) return;

            var map = new Dictionary<Action<string?>, string?>
            {
                { v => Steps = v, _metadata.Steps.ToString() },
                { v => Sampler = v, _metadata.Sampler },
                { v => ScheduleType = v, _metadata.ScheduleType },
                { v => CfgScale = v, _metadata.CFGScale.ToString() },
                { v => Seed = v, _metadata.Seed.ToString() },
                { v => FaceRestoration = v, _metadata.FaceRestoration },
                { v => Size = v, _metadata.Width > 0 && _metadata.Height > 0 ? $"{_metadata.Width}x{_metadata.Height}" : string.Empty },
                { v => ModelHash = v, _metadata.ModelHash },
                { v => Model = v, _metadata.Model },
                { v => Ti = v, _metadata.TI },
                { v => Version = v, _metadata.Version },
                { v => SourceIdentifier = v, _metadata.SourceIdentifier },
                { v => LoRAHashes = v, _metadata.LoRAHashes },
                { v => Width = v, _metadata.Width.ToString() },
                { v => Height = v, _metadata.Height.ToString() },
                { v => Hashes = v, _metadata.Hashes },
                { v => Resources = v, _metadata.Resources }
            };

            foreach (var pair in map)
                pair.Key(pair.Value);
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
            pngMeta.TextData.Add(new PngTextData("parameters", parameters, string.Empty, string.Empty));
            image.Save(path);
        }

        private async Task OnSaveAsync()
        {
            if (string.IsNullOrEmpty(_currentImagePath))
            {
                Log("no image to save", LogSeverity.Error);
                return;
            }
            if (File.Exists(_currentImagePath))
            {
                var confirm = await DialogService.ShowOverwriteConfirmationAsync();
                if (!confirm) return;
            }
            SaveImage(_currentImagePath);
            Log($"image saved {_currentImagePath}", LogSeverity.Success);
        }

        private async Task OnSaveAsAsync()
        {
            if (string.IsNullOrEmpty(_currentImagePath) || _window == null)
            {
                Log("no image to save", LogSeverity.Error);
                return;
            }

            var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = Path.GetFileName(_currentImagePath),
                FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } }
            });

            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                SaveImage(path);
                Log($"image saved {path}", LogSeverity.Success);
            }
            else
            {
                Log("Save as... cancelled by user", LogSeverity.Warning);
            }
        }


        private void OnApplyBlacklist()
        {
            var words = SinglePromptVm.Blacklist?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => w.ToLowerInvariant())
                .ToArray() ?? Array.Empty<string>();

            if (!String.IsNullOrWhiteSpace(SinglePromptVm.Prompt))
            {
                string prompt = ApplyBlacklist(SinglePromptVm.Prompt, words);
                string whitelist = SinglePromptVm.Whitelist ?? string.Empty;
                SinglePromptVm.Prompt = AppendWhitelist(prompt, whitelist);
                Log("whitelist applied", LogSeverity.Success);
            }
            else
            { 
                Log("no prompt to apply blacklist", LogSeverity.Warning);
            }

            if (SinglePromptVm.NegativePrompt != null)
            {
                SinglePromptVm.NegativePrompt = ApplyBlacklist(SinglePromptVm.NegativePrompt, words);
            }
            else
            {
                Log("no negative prompt to apply blacklist", LogSeverity.Warning);
            }

        }

        private string ApplyBlacklist(string text, string[] words)
        {
            if (string.IsNullOrWhiteSpace(text) || words.Length == 0)
            {
                Log("no text or blacklist words provided", LogSeverity.Warning);
                return text;
            }
                
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

        private async Task OnCopyMetadataAsync()
        {
            if (_metadata == null)
            {
                Log("no metadata to copy", LogSeverity.Warning);
                return;
            }
            
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
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow is { Clipboard: { } clipboard })
            {
                await clipboard.SetTextAsync(json);
                Log("metadata copied", LogSeverity.Success);
            }
        }
    }
}
