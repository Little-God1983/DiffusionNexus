using DiffusionNexus.Service.Classes;
using Serilog;

namespace DiffusionNexus.Service.Services.IO;

/// <summary>
/// Simple console-based implementation of <see cref="IProgressReporter"/>.
/// </summary>
public class ConsoleProgressReporter : IProgressReporter
{
    /// <inheritdoc />
    public void Report(ProgressReport report)
    {
        var message = report.StatusMessage ?? string.Empty;
        Log.Information("{Percent}% {Message}", report.Percentage, message);
    }
}
