namespace DiffusionNexus.UI.Services.Startup;

public enum StartupCheckState { Pending, Running, Done, Failed }

/// <summary>
/// One row of the startup ready-check list. Mutable state is owned by
/// <see cref="StartupProgressService"/>; consumers only read.
/// </summary>
public sealed class StartupCheck
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>False for checks that must never delay overlay dismissal (Updates).</summary>
    public bool GatesReadiness { get; init; } = true;

    public StartupCheckState State { get; internal set; } = StartupCheckState.Pending;
    public string? Error { get; internal set; }
}
