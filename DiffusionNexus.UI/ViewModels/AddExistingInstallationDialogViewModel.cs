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

    /// <summary>
    /// True when editing an existing installation (changes dialog title/button text).
    /// </summary>
    [ObservableProperty]
    private bool _isEditMode;

    /// <summary>
    /// The image output folder path for this installation.
    /// Linked to an ImageGallery record via FK.
    /// </summary>
    [ObservableProperty]
    private string _outputFolderPath = string.Empty;

    public ObservableCollection<InstallerType> AvailableTypes { get; } = new(Enum.GetValues<InstallerType>());

    public ObservableCollection<string> FoundExecutables { get; } = new();

    private readonly IDialogService? _dialogService;

    public event Func<Task>? BrowseExecutableRequest;

    public AddExistingInstallationDialogViewModel(string initialPath, IDialogService? dialogService = null)
    {
        _initialPath = initialPath;
        _dialogService = dialogService;
        InstallationPath = initialPath;

        ScanForExecutables();
        InferType();
        InferName();
        DetectOutputFolder();
    }

    /// <summary>
    /// Edit-mode constructor: pre-fills all fields from existing values.
    /// Skips auto-detection so user values are preserved.
    /// </summary>
    public AddExistingInstallationDialogViewModel(
        string name,
        string installationPath,
        InstallerType type,
        string executablePath,
        string outputFolderPath,
        IDialogService? dialogService = null)
    {
        _initialPath = installationPath;
        _dialogService = dialogService;
        InstallationPath = installationPath;
        Name = name;
        SelectedType = type;
        IsEditMode = true;

        ScanForExecutables();

        // Use the provided executable, falling back to the scanned list
        SelectedExecutable = !string.IsNullOrWhiteSpace(executablePath)
            ? executablePath
            : FoundExecutables.FirstOrDefault() ?? string.Empty;

        OutputFolderPath = !string.IsNullOrWhiteSpace(outputFolderPath)
            ? outputFolderPath
            : string.Empty;
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
    /// Sets the display name based on the detected type.
    /// Falls back to the folder name if the type is unknown.
    /// </summary>
    private void InferName()
    {
        Name = SelectedType switch
        {
            InstallerType.Automatic1111 => "Stable Diffusion WebUI",
            InstallerType.Forge => "Stable Diffusion WebUI Forge",
            InstallerType.ComfyUI => "ComfyUI",
            InstallerType.Fooocus => "Fooocus",
            InstallerType.InvokeAI => "InvokeAI",
            InstallerType.FluxGym => "FluxGym",
            InstallerType.SwarmUI => "SwarmUI",
            _ => Path.GetFileName(_initialPath) ?? "New Installation"
        };
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

        // InvokeAI (git clone has invokeai/ subfolder; official installer has invokeai.yaml config)
        if (Directory.Exists(Path.Combine(path, "invokeai"))
            || File.Exists(Path.Combine(path, "invokeai.yaml"))
            || File.Exists(Path.Combine(path, "invoke.bat"))
            || File.Exists(Path.Combine(path, "invoke.sh")))
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

    /// <summary>
    /// Detects the default image output folder based on the detected installer type.
    /// For ComfyUI, parses the startup bat for --output-directory.
    /// For InvokeAI, parses invokeai.yaml for outdir.
    /// For others, uses the conventional subfolder.
    /// </summary>
    private void DetectOutputFolder()
    {
        // 1. For ComfyUI, try parsing the bat file for --output-directory
        if (SelectedType == InstallerType.ComfyUI)
        {
            var batPath = ParseOutputDirectoryFromBat();
            if (!string.IsNullOrWhiteSpace(batPath))
            {
                OutputFolderPath = batPath;
                return;
            }
        }

        // 2. For InvokeAI, try parsing invokeai.yaml for outdir
        if (SelectedType == InstallerType.InvokeAI)
        {
            var yamlPath = ParseInvokeAiOutputDir();
            if (!string.IsNullOrWhiteSpace(yamlPath))
            {
                OutputFolderPath = yamlPath;
                return;
            }
        }

        // 3. Use the conventional default subfolder per type
        var defaultSubfolder = SelectedType switch
        {
            InstallerType.ComfyUI => "output",
            InstallerType.Automatic1111 => "outputs",
            InstallerType.Forge => "outputs",
            InstallerType.Fooocus => "outputs",
            InstallerType.InvokeAI => Path.Combine("invokeai", "outputs"),
            InstallerType.SwarmUI => "Output",
            _ => "outputs"
        };

        // For InvokeAI official installer, outputs/ may be at root level
        if (SelectedType == InstallerType.InvokeAI
            && !Directory.Exists(Path.Combine(_initialPath, "invokeai"))
            && Directory.Exists(Path.Combine(_initialPath, "outputs")))
        {
            defaultSubfolder = "outputs";
        }

        var defaultPath = Path.Combine(_initialPath, defaultSubfolder);
        OutputFolderPath = defaultPath;
    }

    /// <summary>
    /// Parses the selected startup bat file for a --output-directory argument.
    /// Supports both --output-directory=PATH and --output-directory PATH forms.
    /// </summary>
    private string? ParseOutputDirectoryFromBat()
    {
        if (string.IsNullOrWhiteSpace(SelectedExecutable))
            return null;

        var batPath = Path.Combine(_initialPath, SelectedExecutable);
        if (!File.Exists(batPath))
            return null;

        try
        {
            foreach (var rawLine in File.ReadLines(batPath))
            {
                var line = rawLine.Trim();

                // Match --output-directory=PATH or --output-directory PATH
                var argIndex = line.IndexOf("--output-directory", StringComparison.OrdinalIgnoreCase);
                if (argIndex < 0)
                    continue;

                var afterArg = line[(argIndex + "--output-directory".Length)..];

                string rawPath;
                if (afterArg.StartsWith('='))
                {
                    // --output-directory=PATH
                    rawPath = afterArg[1..].Trim();
                }
                else if (afterArg.StartsWith(' '))
                {
                    // --output-directory PATH
                    rawPath = afterArg.Trim();
                }
                else
                {
                    continue;
                }

                // Handle quoted paths and trim trailing arguments
                rawPath = rawPath.Trim('"', '\'');

                // Take only up to the next space-dash (next argument)
                var nextArgIndex = rawPath.IndexOf(" --", StringComparison.Ordinal);
                if (nextArgIndex >= 0)
                    rawPath = rawPath[..nextArgIndex].TrimEnd();

                if (!string.IsNullOrWhiteSpace(rawPath))
                    return rawPath;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Failed to parse bat file for --output-directory: {Path}", batPath);
        }

        return null;
    }

    /// <summary>
    /// Parses invokeai.yaml for the output directory.
    /// The YAML structure is: paths: outdir: "path/to/outputs"
    /// Uses simple line parsing to avoid adding a YAML library dependency.
    /// </summary>
    private string? ParseInvokeAiOutputDir()
    {
        var yamlPath = Path.Combine(_initialPath, "invokeai.yaml");
        if (!File.Exists(yamlPath))
            return null;

        try
        {
            var inPaths = false;
            foreach (var rawLine in File.ReadLines(yamlPath))
            {
                var line = rawLine.TrimEnd();

                // Detect "paths:" section (no leading whitespace or top-level)
                if (line.TrimStart().StartsWith("paths:", StringComparison.OrdinalIgnoreCase)
                    && !line.StartsWith(' ') && !line.StartsWith('\t'))
                {
                    inPaths = true;
                    continue;
                }

                // New top-level section — stop
                if (inPaths && line.Length > 0 && !line.StartsWith(' ') && !line.StartsWith('\t'))
                    break;

                // Look for "outdir:" or "outputs_dir:" under paths
                if (inPaths)
                {
                    var trimmed = line.Trim();
                    string? value = null;

                    if (trimmed.StartsWith("outdir:", StringComparison.OrdinalIgnoreCase))
                        value = trimmed["outdir:".Length..].Trim().Trim('"', '\'');
                    else if (trimmed.StartsWith("outputs_dir:", StringComparison.OrdinalIgnoreCase))
                        value = trimmed["outputs_dir:".Length..].Trim().Trim('"', '\'');

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        // Resolve relative paths against the installation directory
                        return Path.IsPathRooted(value) ? value : Path.Combine(_initialPath, value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Failed to parse invokeai.yaml for outdir: {Path}", yamlPath);
        }

        return null;
    }

    [RelayCommand]
    private async Task BrowseOutputFolderAsync()
    {
        if (_dialogService is null) return;

        var folder = await _dialogService.ShowOpenFolderDialogAsync("Select Image Output Folder");
        if (!string.IsNullOrEmpty(folder))
        {
            OutputFolderPath = folder;
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
