using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Interfaces;
using DiffusionNexus.DataAccess.Repositories;

namespace DiffusionNexus.DataAccess;

public class UnitOfWork : IUnitOfWork, IDisposable
{
    private readonly DiffusionNexusDbContext _context;
    private ModelRepository? _modelRepository;
    private ModelFileRepository? _modelFileRepository;

    public UnitOfWork(DiffusionNexusDbContext context)
    {
        _context = context;
    }

    public ModelRepository Models => _modelRepository ??= new ModelRepository(_context);
    public ModelFileRepository ModelFiles => _modelFileRepository ??= new ModelFileRepository(_context);

    public IRepository<T> Repository<T>() where T : class
    {
        return new Repository<T>(_context);
    }

    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
