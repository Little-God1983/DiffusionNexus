using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Repositories;

public class ModelFileRepository : Repository<ModelFile>
{
    public ModelFileRepository(DiffusionNexusDbContext context) : base(context)
    {
    }

    public async Task<ModelFile?> GetByLocalFilePathAsync(string localFilePath)
    {
        return await _dbSet
            .Include(f => f.ModelVersion)
                .ThenInclude(v => v.Model)
            .FirstOrDefaultAsync(f => f.LocalFilePath == localFilePath);
    }

    public async Task<ModelFile?> GetBySHA256HashAsync(string sha256Hash)
    {
        return await _dbSet
            .Include(f => f.ModelVersion)
                .ThenInclude(v => v.Model)
            .Include(f => f.ModelVersion)
                .ThenInclude(v => v.TrainedWords)
            .FirstOrDefaultAsync(f => f.SHA256Hash == sha256Hash);
    }

    public async Task<IEnumerable<ModelFile>> GetLocalFilesAsync()
    {
        return await _dbSet
            .Where(f => f.LocalFilePath != null)
            .Include(f => f.ModelVersion)
                .ThenInclude(v => v.Model)
            .ToListAsync();
    }
}
