namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Service for managing disclaimer acceptance.
/// </summary>
public interface IDisclaimerService
{
    /// <summary>
    /// Checks if the current Windows user has accepted the disclaimer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the user has accepted the disclaimer; otherwise false.</returns>
    Task<bool> HasUserAcceptedDisclaimerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the current Windows user's acceptance of the disclaimer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AcceptDisclaimerAsync(CancellationToken cancellationToken = default);
}
