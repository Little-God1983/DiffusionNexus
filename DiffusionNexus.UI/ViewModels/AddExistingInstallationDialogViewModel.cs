using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

public partial class AddExistingInstallationDialogViewModel : ViewModelBase
{
    private readonly string _initialPath;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _installationPath = string.Empty;

    [ObservableProperty]
    private InstallerType _selectedType = InstallerType.Unknown;

    [ObservableProperty]
    private string _selectedExecutable = string.Empty;

    [ObservableProperty]
    private bool _isCustomExecutable;

    public ObservableCollection<InstallerType> AvailableTypes { get; } = new(Enum.GetValues<InstallerType>());
    
    public ObservableCollection<string> FoundExecutables { get; } = new();

    private readonly IDialogService? _dialogService;
    
    public event Func<Task>? BrowseExecutableRequest;

    public AddExistingInstallationDialogViewModel(string initialPath, IDialogService? dialogService = null)
    {
        _initialPath = initialPath;
        _dialogService = dialogService;
        InstallationPath = initialPath;
        
        // Default name to folder name
        Name = Path.GetFileName(initialPath) ?? "New Installation";

        ScanForExecutables();
        InferType();
    }

    private void ScanForExecutables()
    {
        try
        {
            if (Directory.Exists(_initialPath))
            {
                var files = Directory.GetFiles(_initialPath, "*.bat"); // Windows focus as per user request
                foreach (var file in files)
                {
                    FoundExecutables.Add(Path.GetFileName(file));
                }
            }
        }
        catch
        {
            // Ignore access errors
        }

        if (FoundExecutables.Count > 0)
        {
            SelectedExecutable = FoundExecutables[0];
        }
    }

    private void InferType()
    {
        // Simple heuristic
        if (File.Exists(Path.Combine(_initialPath, "webui-user.bat")))
        {
            SelectedType = InstallerType.Automatic1111;
            SelectedExecutable = "webui-user.bat";
        }
        else if (File.Exists(Path.Combine(_initialPath, "run_nvidia_gpu.bat")))
        {
            SelectedType = InstallerType.ComfyUI;
            SelectedExecutable = "run_nvidia_gpu.bat";
        }
        else if (File.Exists(Path.Combine(_initialPath, "run_cpu.bat")))
        {
            SelectedType = InstallerType.ComfyUI;
             // Don't override if nvidia was found (handled by order or logic, but here simple)
        }
    }

    [RelayCommand]
    private async Task BrowseExecutable()
    {
        if (_dialogService is not null)
        {
            var file = await _dialogService.ShowOpenFileDialogAsync("Select Executable", _initialPath, "*.bat");
            if (file != null)
            {
                 // Should we check if it's inside the folder?
                 // If outside, maybe warn? For now assume user knows best or we store full path.
                 // If inside, store relative.
                 if (file.StartsWith(_initialPath, StringComparison.OrdinalIgnoreCase))
                 {
                     SelectedExecutable = Path.GetFileName(file);
                 }
                 else
                 {
                     SelectedExecutable = file;
                 }
            }
        }
        else if (BrowseExecutableRequest != null)
        {
             await BrowseExecutableRequest.Invoke();
        }
    }
}
