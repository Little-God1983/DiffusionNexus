using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services.IO;

/// <summary>
/// Defines progress reporting independent of UI frameworks.
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    /// Reports a progress update.
    /// </summary>
    void Report(ProgressReport report);
}
