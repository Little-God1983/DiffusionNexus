using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for managing disclaimer acceptance persistence.
/// </summary>
public class DisclaimerService : IDisclaimerService
{
    private readonly IUnitOfWork _unitOfWork;

    public DisclaimerService(IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public async Task<bool> HasUserAcceptedDisclaimerAsync(CancellationToken cancellationToken = default)
    {
        var username = GetCurrentWindowsUsername();

        return await _unitOfWork.DisclaimerAcceptances
            .HasUserAcceptedAsync(username, cancellationToken)
            .ConfigureAwait(false);
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

        await _unitOfWork.DisclaimerAcceptances
            .AddAsync(acceptance, cancellationToken)
            .ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetCurrentWindowsUsername()
    {
        return Environment.UserName;
    }
}
