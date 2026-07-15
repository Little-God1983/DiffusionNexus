using System;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Suppresses redundant backup progress reports. A report passes only when the
/// integer percentage or the phase changed. This bounds UI dispatcher traffic
/// to ~100 posts per backup regardless of dataset file count — reporting every
/// N files froze the UI on many-small-file datasets (dispatcher storm).
/// </summary>
public sealed class BackupProgressGate
{
    private int _lastPercent = -1;
    private string? _lastPhase;

    public bool ShouldReport(int percent, string? phase)
    {
        if (percent == _lastPercent && string.Equals(phase, _lastPhase, StringComparison.Ordinal))
            return false;

        _lastPercent = percent;
        _lastPhase = phase;
        return true;
    }
}
