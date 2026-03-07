namespace DiffusionNexus.Domain.Services.UnifiedLogging;

/// <summary>
/// Categorizes log entries by functional area for filtering and grouping.
/// </summary>
public enum LogCategory
{
    /// <summary>General application messages.</summary>
    General,

    /// <summary>Dataset and settings backup operations.</summary>
    Backup,

    /// <summary>File download operations.</summary>
    Download,

    /// <summary>Software installation and update operations.</summary>
    Installation,

    /// <summary>Instance lifecycle management (start, stop, stdout/stderr).</summary>
    InstanceManagement,

    /// <summary>Mod and workload management.</summary>
    ModManagement,

    /// <summary>Network and HTTP operations.</summary>
    Network,

    /// <summary>File system operations (copy, delete, move).</summary>
    FileSystem,

    /// <summary>Application and instance configuration.</summary>
    Configuration
}
