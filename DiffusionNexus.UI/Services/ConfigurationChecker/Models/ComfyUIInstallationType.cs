namespace DiffusionNexus.UI.Services.ConfigurationChecker.Models;

/// <summary>
/// Distinguishes between manual and portable ComfyUI installs.
/// Portable installs have a <c>ComfyUI</c> subfolder under the root,
/// while manual installs have <c>models</c> and <c>custom_nodes</c> directly at root.
/// </summary>
public enum ComfyUIInstallationType
{
    /// <summary>
    /// Manual install: root contains <c>models/</c>, <c>custom_nodes/</c>, <c>main.py</c> etc.
    /// Typical layout: <c>C:\ComfyUI\models\checkpoints</c>
    /// </summary>
    Manual,

    /// <summary>
    /// Windows portable: root contains a <c>ComfyUI\</c> subfolder with the actual code.
    /// Typical layout: <c>C:\ComfyUI_windows_portable\ComfyUI\models\checkpoints</c>
    /// </summary>
    Portable
}
