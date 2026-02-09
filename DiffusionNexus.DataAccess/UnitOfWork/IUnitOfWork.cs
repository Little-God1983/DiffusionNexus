using DiffusionNexus.DataAccess.Repositories.Interfaces;

namespace DiffusionNexus.DataAccess.UnitOfWork;

/// <summary>
/// Coordinates transactions across multiple repositories sharing a single DbContext.
/// </summary>
public interface IUnitOfWork : IDisposable, IAsyncDisposable
{
    /// <summary>Repository for Model entities.</summary>
    IModelRepository Models { get; }

    /// <summary>Repository for ModelFile entities.</summary>
    IModelFileRepository ModelFiles { get; }

    /// <summary>Repository for AppSettings entities.</summary>
    IAppSettingsRepository AppSettings { get; }

    /// <summary>Repository for DisclaimerAcceptance entities.</summary>
    IDisclaimerAcceptanceRepository DisclaimerAcceptances { get; }

    /// <summary>
    /// Persists all pending changes to the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of state entries written to the database.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a database transaction.
    /// </summary>
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the current transaction.
    /// </summary>
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the current transaction.
    /// </summary>
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}
