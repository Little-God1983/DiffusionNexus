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
        // 1. Try git remote URL first (most reliable)
        var gitType = DetectTypeFromGitRemote(_initialPath);
        if (gitType != InstallerType.Unknown)
        {
            SelectedType = gitType;
            SelectBestExecutable();
            return;
        }

        // 2. Fall back to signature files/folders
        var signatureType = DetectTypeFromSignatureFiles(_initialPath);
        if (signatureType != InstallerType.Unknown)
        {
            SelectedType = signatureType;
            SelectBestExecutable();
            return;
        }

        // 3. Last resort: executable name heuristics
        SelectBestExecutable();
    }

    /// <summary>
    /// Reads .git/config to extract the remote origin URL and matches it against known repositories.
    /// </summary>
    private static InstallerType DetectTypeFromGitRemote(string path)
    {
        // TODO: Linux Implementation - path separators work on both, but verify
        var gitConfigPath = Path.Combine(path, ".git", "config");
        if (!File.Exists(gitConfigPath))
            return InstallerType.Unknown;

        try
        {
            var content = File.ReadAllText(gitConfigPath);
            var remoteUrl = ExtractGitRemoteUrl(content);
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return InstallerType.Unknown;

            // Order matters — Forge check must come before A1111 since Forge is a fork
            return remoteUrl switch
            {
                _ when ContainsIgnoreCase(remoteUrl, "stable-diffusion-webui-forge")
                    => InstallerType.Forge,
                _ when ContainsIgnoreCase(remoteUrl, "stable-diffusion-webui-reforge")
                    => InstallerType.Forge,
                _ when ContainsIgnoreCase(remoteUrl, "stable-diffusion-webui")
                    => InstallerType.Automatic1111,
                _ when ContainsIgnoreCase(remoteUrl, "ComfyUI")
                    => InstallerType.ComfyUI,
                _ when ContainsIgnoreCase(remoteUrl, "Fooocus")
                    => InstallerType.Fooocus,
                _ when ContainsIgnoreCase(remoteUrl, "InvokeAI")
                    => InstallerType.InvokeAI,
                _ when ContainsIgnoreCase(remoteUrl, "FluxGym")
                    => InstallerType.FluxGym,
                _ when ContainsIgnoreCase(remoteUrl, "SwarmUI")
                    => InstallerType.SwarmUI,
                _ => InstallerType.Unknown
            };
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Failed to read git config at {Path}", gitConfigPath);
            return InstallerType.Unknown;
        }
    }

    /// <summary>
    /// Extracts the first remote origin URL from a .git/config file content.
    /// Looks for: url = https://github.com/user/repo.git
    /// </summary>
    private static string? ExtractGitRemoteUrl(string gitConfig)
    {
        var inRemoteOrigin = false;
        foreach (var rawLine in gitConfig.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase))
            {
                inRemoteOrigin = true;
                continue;
            }

            // New section starts — stop looking
            if (inRemoteOrigin && line.StartsWith('['))
                break;

            if (inRemoteOrigin && line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                var eqIndex = line.IndexOf('=');
                if (eqIndex >= 0)
                    return line[(eqIndex + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Detects the installer type by looking for files/folders unique to each project.
    /// </summary>
    private static InstallerType DetectTypeFromSignatureFiles(string path)
    {
        // Forge has modules_forge/ — check before A1111 since both have webui.py
        if (Directory.Exists(Path.Combine(path, "modules_forge")))
            return InstallerType.Forge;

        // A1111 has modules/ and webui.py but no modules_forge/
        if (Directory.Exists(Path.Combine(path, "modules"))
            && File.Exists(Path.Combine(path, "webui.py")))
            return InstallerType.Automatic1111;

        // ComfyUI has comfy/ and main.py
        if (Directory.Exists(Path.Combine(path, "comfy"))
            || Directory.Exists(Path.Combine(path, "comfy_extras")))
            return InstallerType.ComfyUI;

        // Fooocus
        if (File.Exists(Path.Combine(path, "fooocus_version.py")))
            return InstallerType.Fooocus;

        // InvokeAI
        if (Directory.Exists(Path.Combine(path, "invokeai")))
            return InstallerType.InvokeAI;

        // SwarmUI
        if (Directory.Exists(Path.Combine(path, "launchtools"))
            && Directory.Exists(Path.Combine(path, "src")))
            return InstallerType.SwarmUI;

        return InstallerType.Unknown;
    }

    /// <summary>
    /// Selects the best executable from the found list based on the detected type.
    /// </summary>
    private void SelectBestExecutable()
    {
        // Preferred executable per type
        var preferred = SelectedType switch
        {
            InstallerType.Automatic1111 => "webui-user.bat",
            InstallerType.Forge => "webui-user.bat",
            InstallerType.ComfyUI => "run_nvidia_gpu.bat",
            InstallerType.Fooocus => "run.bat",
            InstallerType.InvokeAI => "invoke.bat",
            InstallerType.FluxGym => "run.bat",
            InstallerType.SwarmUI => "launch-windows.bat",
            _ => null
        };

        if (preferred is not null && FoundExecutables.Contains(preferred))
        {
            SelectedExecutable = preferred;
        }
    }

    private static bool ContainsIgnoreCase(string source, string value) =>
        source.Contains(value, StringComparison.OrdinalIgnoreCase);

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
