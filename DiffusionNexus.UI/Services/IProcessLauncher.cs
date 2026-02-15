namespace DiffusionNexus.UI.Services;

/// <summary>
/// Abstracts launching external processes (e.g. Explorer) so that ViewModels
/// remain testable without spawning real OS windows.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>
    /// Opens the system file explorer and selects the specified file.
    /// </summary>
    /// <param name="filePath">Full path to the file to select.</param>
    // TODO: Linux Implementation for selecting a file in the file manager
    void OpenFolderAndSelectFile(string filePath);

    /// <summary>
    /// Opens the system file explorer at the specified folder.
    /// </summary>
    /// <param name="folderPath">Full path to the folder to open.</param>
    // TODO: Linux Implementation for opening a folder in the file manager
    void OpenFolder(string folderPath);
}
