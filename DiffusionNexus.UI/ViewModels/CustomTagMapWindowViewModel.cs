using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Helper;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels;

public partial class CustomTagMapWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? tags;

    [ObservableProperty]
    private string? folder;

    private readonly CustomTagMapXmlService _xmlService = new();
    private Window? _window;
    private CustomTagMap? _editingMap;

    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public CustomTagMapWindowViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(Cancel);
    }

    public void SetMapping(CustomTagMap map)
    {
        _editingMap = map;
        Tags = string.Join(", ", map.LookForTag);
        Folder = map.MapToFolder;
    }

    public void SetWindow(Window window)
    {
        _window = window;
    }

    private void Cancel()
    {
        _window?.Close(false);
    }

    private async Task SaveAsync()
    {
        var tagList = (Tags ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (tagList.Count == 0 || string.IsNullOrWhiteSpace(Folder))
        {
            if (_window != null)
            {
                var warn = new Window
                {
                    Width = 300,
                    Height = 150,
                    Title = "Warning",
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                var ok = new Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Center };
                ok.Click += (_, _) => warn.Close();
                warn.Content = new StackPanel
                {
                    Margin = new Thickness(10),
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Please enter at least one tag and a target folder.", TextWrapping = TextWrapping.Wrap },
                        ok
                    }
                };
                await warn.ShowDialog(_window);
            }
            return;
        }

        var mappings = _xmlService.LoadMappings();

        if (_editingMap != null)
        {
            var existing = mappings.FirstOrDefault(m => m.Priority == _editingMap.Priority);
            if (existing != null)
            {
                existing.LookForTag = tagList;
                existing.MapToFolder = Folder!.Trim();
            }
        }
        else
        {
            var max = mappings.Any() ? mappings.Max(m => m.Priority) : 0;
            mappings.Add(new CustomTagMap
            {
                LookForTag = tagList,
                MapToFolder = Folder!.Trim(),
                Priority = max + 1
            });
        }

        mappings = CustomTagMapPriorityHelper.Normalize(mappings);
        _xmlService.SaveMappings(mappings);
        _window?.Close(true);
    }
}
