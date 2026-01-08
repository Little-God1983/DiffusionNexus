using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for managing disclaimer acceptance persistence.
/// </summary>
public class DisclaimerService : IDisclaimerService
{
    private readonly DiffusionNexusCoreDbContext _dbContext;

    public DisclaimerService(DiffusionNexusCoreDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <inheritdoc />
    public async Task<bool> HasUserAcceptedDisclaimerAsync(CancellationToken cancellationToken = default)
    {
        var username = GetCurrentWindowsUsername();

        return await _dbContext.DisclaimerAcceptances
            .AnyAsync(d => d.WindowsUsername == username && d.Accepted, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AcceptDisclaimerAsync(CancellationToken cancellationToken = default)
    {
        var username = GetCurrentWindowsUsername();

        var acceptance = new DisclaimerAcceptance
        {
            WindowsUsername = username,
            AcceptedAt = DateTimeOffset.UtcNow,
            Accepted = true
        };

        _dbContext.DisclaimerAcceptances.Add(acceptance);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string GetCurrentWindowsUsername()
    {
        return Environment.UserName;
    }
}
