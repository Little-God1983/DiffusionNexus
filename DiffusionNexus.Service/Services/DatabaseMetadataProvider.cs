using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories;
using DiffusionNexus.Service.Classes;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace DiffusionNexus.Service.Services;

public class DatabaseMetadataProvider : IModelMetadataProvider
{
    private readonly DiffusionNexusDbContext _context;
    private readonly ModelFileRepository _fileRepository;

    public DatabaseMetadataProvider(DiffusionNexusDbContext context)
    {
        _context = context;
        _fileRepository = new ModelFileRepository(context);
    }

    public async Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(identifier))
            return false;

        var hash = await Task.Run(() => ComputeSHA256(identifier), cancellationToken);
        var file = await _fileRepository.GetBySHA256HashAsync(hash);
        return file != null;
    }

    public async Task<ModelClass> GetModelMetadataAsync(string filePath, CancellationToken cancellationToken = default, ModelClass? model = null)
    {
        model ??= new ModelClass();
        model.SafeTensorFileName = Path.GetFileNameWithoutExtension(filePath);

        string hash = await Task.Run(() => ComputeSHA256(filePath), cancellationToken);
        model.SHA256Hash = hash;

        var dbFile = await _fileRepository.GetBySHA256HashAsync(hash);
        if (dbFile == null)
        {
            model.NoMetaData = true;
            return model;
        }

        var dbModel = dbFile.ModelVersion.Model;
        var dbVersion = dbFile.ModelVersion;

        model.ModelId = dbModel.CivitaiModelId;
        model.DiffusionBaseModel = dbVersion.BaseModel;
        model.ModelVersionName = dbVersion.Name;
        model.ModelType = ParseModelType(dbModel.Type);
        model.Nsfw = dbModel.Nsfw;

        var tags = await _context.ModelTags
            .Where(t => t.ModelId == dbModel.Id)
            .Select(t => t.Tag)
            .ToListAsync(cancellationToken);
        model.Tags = tags;

        var trainedWords = dbVersion.TrainedWords.Select(w => w.Word).ToList();
        model.TrainedWords = trainedWords;

        model.CivitaiCategory = MetaDataUtilService.GetCategoryFromTags(model.Tags);
        model.NoMetaData = !model.HasAnyMetadata;

        return model;
    }

    private static string ComputeSHA256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return string.Concat(hash.Select(b => b.ToString("x2")));
    }

    private static DiffusionTypes ParseModelType(string? typeToken)
    {
        if (string.IsNullOrWhiteSpace(typeToken))
            return DiffusionTypes.UNASSIGNED;
        return Enum.TryParse(typeToken.Replace(" ", string.Empty), true, out DiffusionTypes dt)
            ? dt
            : DiffusionTypes.UNASSIGNED;
    }
}
