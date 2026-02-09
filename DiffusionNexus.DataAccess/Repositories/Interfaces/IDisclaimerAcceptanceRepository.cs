using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.DataAccess.Repositories.Interfaces;

/// <summary>
/// Repository for <see cref="DisclaimerAcceptance"/> entities.
/// </summary>
public interface IDisclaimerAcceptanceRepository : IRepository<DisclaimerAcceptance>
{
    /// <summary>
    /// Checks whether a user has accepted the disclaimer.
    /// </summary>
    /// <param name="windowsUsername">The Windows username to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the user has an accepted record.</returns>
    Task<bool> HasUserAcceptedAsync(string windowsUsername, CancellationToken cancellationToken = default);
}
