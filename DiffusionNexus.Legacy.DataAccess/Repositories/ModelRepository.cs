using DiffusionNexus.Legacy.DataAccess.Data;
using DiffusionNexus.Legacy.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Legacy.DataAccess.Repositories;

public class ModelRepository : Repository<Model>
{
    public ModelRepository(DiffusionNexusDbContext context) : base(context)
    {
    }

    public async Task<Model?> GetByCivitaiIdAsync(string civitaiModelId)
    {
        return await _dbSet
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TrainedWords)
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.CivitaiModelId == civitaiModelId);
    }

    public async Task<Model?> GetByIdWithDetailsAsync(int id)
    {
        return await _dbSet
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TrainedWords)
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<IEnumerable<Model>> GetAllWithDetailsAsync()
    {
        return await _dbSet
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Tags)
            .ToListAsync();
    }
}
