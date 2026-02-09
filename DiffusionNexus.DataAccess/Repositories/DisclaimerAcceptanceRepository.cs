using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Repositories;

/// <summary>
/// Repository for <see cref="DisclaimerAcceptance"/> entities.
/// </summary>
internal sealed class DisclaimerAcceptanceRepository : RepositoryBase<DisclaimerAcceptance>, IDisclaimerAcceptanceRepository
{
    public DisclaimerAcceptanceRepository(DiffusionNexusCoreDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<bool> HasUserAcceptedAsync(string windowsUsername, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .AnyAsync(d => d.WindowsUsername == windowsUsername && d.Accepted, cancellationToken)
            .ConfigureAwait(false);
    }
}
