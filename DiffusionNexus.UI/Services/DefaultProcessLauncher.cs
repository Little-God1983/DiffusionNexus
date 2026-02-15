using System.Diagnostics;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Default implementation that launches Windows Explorer.
/// </summary>
// TODO: Linux Implementation for file manager process launching
internal sealed class DefaultProcessLauncher : IProcessLauncher
{
    /// <inheritdoc />
    public void OpenFolderAndSelectFile(string filePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{filePath}\"",
            UseShellExecute = true
        });
    }

    /// <inheritdoc />
    public void OpenFolder(string folderPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = folderPath,
            UseShellExecute = true
        });
    }
}
