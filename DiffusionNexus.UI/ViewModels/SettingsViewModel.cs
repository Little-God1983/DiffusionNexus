using System;
using Avalonia;
using Avalonia.Controls;
using ReactiveUI;
using System.Reactive;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels
{
    public class SettingsViewModel : ReactiveObject
    {
        private readonly ISettingsService _settingsService;

        private string? _civitaiApiKey;
        public string? CivitaiApiKey
        {
            get => _civitaiApiKey;
            set => this.RaiseAndSetIfChanged(ref _civitaiApiKey, value);
        }

        private string? _loraHelperFolderPath;
        public string? LoraHelperFolderPath
        {
            get => _loraHelperFolderPath;
            set => this.RaiseAndSetIfChanged(ref _loraHelperFolderPath, value);
        }

        public ReactiveCommand<Unit, Unit> BrowseLoraHelperFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearApiKeyCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveSettingsCommand { get; }

        public SettingsViewModel() : this(new SettingsService())
        {
        }

        public SettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            var settings = _settingsService.Load();
            CivitaiApiKey = SecureStorageHelper.Decrypt(settings.EncryptedApiKey ?? string.Empty);
            LoraHelperFolderPath = settings.LoraHelperFolderPath;

            BrowseLoraHelperFolderCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                var dlg = new OpenFolderDialog();
                var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (window != null)
                {
                    var result = await dlg.ShowAsync(window);
                    if (!string.IsNullOrWhiteSpace(result))
                        LoraHelperFolderPath = result;
                }
            });

            ClearApiKeyCommand = ReactiveCommand.Create(() => CivitaiApiKey = string.Empty);

            SaveSettingsCommand = ReactiveCommand.CreateFromTask(async () => await SaveAsync());
        }

        private async System.Threading.Tasks.Task SaveAsync()
        {
            var settings = new AppSettings
            {
                EncryptedApiKey = SecureStorageHelper.Encrypt(CivitaiApiKey ?? string.Empty),
                LoraHelperFolderPath = LoraHelperFolderPath
            };
            _settingsService.Save(settings);

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner != null)
            {
                var messageWindow = new Window
                {
                    Width = 250,
                    Height = 120,
                    Content = BuildMessage("Settings saved")
                };
                await messageWindow.ShowDialog(owner);
            }
        }

        private Control BuildMessage(string msg)
        {
            var text = new TextBlock { Text = msg, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var button = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Width = 60 };
            var panel = new StackPanel { Spacing = 10, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            panel.Children.Add(text);
            panel.Children.Add(button);
            button.Click += (_, __) => (button.GetVisualRoot() as Window)?.Close();
            return panel;
        }
    }
}
