using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Exceptions;
using DiffusionNexus.DataAccess.Repositories;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DiffusionNexus.DataAccess.UnitOfWork;

/// <summary>
/// Coordinates transactions across multiple repositories sharing a single <see cref="DiffusionNexusCoreDbContext"/>.
/// </summary>
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly DiffusionNexusCoreDbContext _context;
    private IDbContextTransaction? _transaction;
    private bool _disposed;

    private IModelRepository? _models;
    private IModelFileRepository? _modelFiles;
    private IAppSettingsRepository? _appSettings;
    private IDisclaimerAcceptanceRepository? _disclaimerAcceptances;
    private IInstallerPackageRepository? _installerPackages;

    public UnitOfWork(DiffusionNexusCoreDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public IModelRepository Models =>
        _models ??= new ModelRepository(_context);

    /// <inheritdoc />
    public IModelFileRepository ModelFiles =>
        _modelFiles ??= new ModelFileRepository(_context);

    /// <inheritdoc />
    public IAppSettingsRepository AppSettings =>
        _appSettings ??= new AppSettingsRepository(_context);

    /// <inheritdoc />
    public IDisclaimerAcceptanceRepository DisclaimerAcceptances =>
        _disclaimerAcceptances ??= new DisclaimerAcceptanceRepository(_context);


    /// <inheritdoc />
    public IInstallerPackageRepository InstallerPackages =>
        _installerPackages ??= new InstallerPackageRepository(_context);

    /// <inheritdoc />
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(
                ex.Entries.FirstOrDefault()?.Metadata.ClrType.Name ?? "Unknown",
                ex);
        }
        catch (DbUpdateException ex)
        {
            throw new DatabaseOperationException(
                "SaveChanges",
                $"Failed to save changes: {ex.InnerException?.Message ?? ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        _transaction = await _context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
            throw new InvalidOperationException("No transaction has been started.");

        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.DisposeAsync().ConfigureAwait(false);
        _transaction = null;
    }

    /// <inheritdoc />
    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
            throw new InvalidOperationException("No transaction has been started.");

        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.DisposeAsync().ConfigureAwait(false);
        _transaction = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _transaction?.Dispose();
        _context.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (_transaction is not null)
            await _transaction.DisposeAsync().ConfigureAwait(false);

        await _context.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
