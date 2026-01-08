using DiffusionNexus.Legacy.DataAccess.Data;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Legacy.Service;

/// <summary>
/// Composite metadata provider that tries database first, then falls back to file-based providers
/// </summary>
public class CompositeMetadataProvider : IModelMetadataProvider
{
    private readonly DiffusionNexusDbContext? _context;
    private readonly IModelMetadataProvider[] _fallbackProviders;

    public CompositeMetadataProvider(DiffusionNexusDbContext? context, params IModelMetadataProvider[] fallbackProviders)
    {
        _context = context;
        _fallbackProviders = fallbackProviders ?? Array.Empty<IModelMetadataProvider>();
    }

    public async Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        if (_context != null && File.Exists(identifier))
        {
            var hash = await ComputeHashAsync(identifier, cancellationToken);
            var exists = await _context.ModelFiles.AnyAsync(f => f.SHA256Hash == hash, cancellationToken);
            if (exists) return true;
        }

        foreach (var provider in _fallbackProviders)
        {
            if (await provider.CanHandleAsync(identifier, cancellationToken))
                return true;
        }

        return false;
    }

    public async Task<ModelClass> GetModelMetadataAsync(string identifier, CancellationToken cancellationToken = default, ModelClass? model = null)
    {
        model ??= new ModelClass();

        if (_context != null && File.Exists(identifier))
        {
            var hash = await ComputeHashAsync(identifier, cancellationToken);
            var dbFile = await _context.ModelFiles
                .Include(f => f.ModelVersion)
                    .ThenInclude(v => v.Model)
                        .ThenInclude(m => m.Tags)
                .Include(f => f.ModelVersion)
                    .ThenInclude(v => v.TrainedWords)
                .FirstOrDefaultAsync(f => f.SHA256Hash == hash, cancellationToken);

            if (dbFile != null)
            {
                var dbModel = ModelMapper.ToModelClass(dbFile.ModelVersion.Model, dbFile.ModelVersion, dbFile);
                
                model.ModelId = dbModel.ModelId;
                model.SafeTensorFileName = dbModel.SafeTensorFileName;
                model.ModelVersionName = dbModel.ModelVersionName;
                model.DiffusionBaseModel = dbModel.DiffusionBaseModel;
                model.ModelType = dbModel.ModelType;
                model.Tags = dbModel.Tags;
                model.TrainedWords = dbModel.TrainedWords;
                model.Nsfw = dbModel.Nsfw;
                model.CivitaiCategory = dbModel.CivitaiCategory;
                model.SHA256Hash = dbModel.SHA256Hash;
                model.NoMetaData = false;

                return model;
            }
        }

        foreach (var provider in _fallbackProviders)
        {
            try
            {
                if (await provider.CanHandleAsync(identifier, cancellationToken))
                {
                    model = await provider.GetModelMetadataAsync(identifier, cancellationToken, model);
                    
                    if (model.HasFullMetadata)
                        break;
                }
            }
            catch
            {
                continue;
            }
        }

        return model;
    }

    private static async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var stream = File.OpenRead(filePath);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }, cancellationToken);
    }
}
