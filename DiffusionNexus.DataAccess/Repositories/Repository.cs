using System.Linq.Expressions;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Repositories;

public class Repository<T> : IRepository<T> where T : class
{
    protected readonly DiffusionNexusDbContext _context;
    protected readonly DbSet<T> _dbSet;

    public Repository(DiffusionNexusDbContext context)
    {
        _context = context;
        _dbSet = context.Set<T>();
    }

    public virtual async Task<T?> GetByKeyAsync(object key)
    {
        return await _dbSet.FindAsync(key);
    }

    public virtual async Task<IEnumerable<T>> ListAsync(Expression<Func<T, bool>>? filter = null)
    {
        IQueryable<T> query = _dbSet;

        if (filter != null)
        {
            query = query.Where(filter);
        }

        return await query.ToListAsync();
    }

    public virtual async Task AddAsync(T entity)
    {
        await _dbSet.AddAsync(entity);
    }

    public virtual void Update(T entity)
    {
        _dbSet.Update(entity);
    }

    public virtual void Delete(T entity)
    {
        _dbSet.Remove(entity);
    }
}
