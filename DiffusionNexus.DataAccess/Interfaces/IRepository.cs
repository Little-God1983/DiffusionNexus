using System.Linq.Expressions;

namespace DiffusionNexus.DataAccess.Interfaces
{
    public interface IRepository<T> where T : class
    {
        Task<T?> GetByKeyAsync(object key);
        Task<IEnumerable<T>> ListAsync(Expression<Func<T, bool>>? filter = null);
        Task AddAsync(T entity);
        void Update(T entity);
        void Delete(T entity);
    }
}
