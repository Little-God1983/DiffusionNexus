namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Records a user's acceptance of the software disclaimer.
/// Each acceptance is stored with the Windows username and timestamp.
/// </summary>
public class DisclaimerAcceptance : BaseEntity
{
    /// <summary>
    /// Windows username of the user who accepted the disclaimer.
    /// </summary>
    public string WindowsUsername { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the disclaimer was accepted.
    /// </summary>
    public DateTimeOffset AcceptedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether the user accepted the disclaimer.
    /// </summary>
    public bool Accepted { get; set; }
}
