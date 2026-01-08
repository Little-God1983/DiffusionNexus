using System.Text.Json;
using DiffusionNexus.Legacy.DataAccess.Data;
using DiffusionNexus.Legacy.DataAccess.Entities;
using DiffusionNexus.Service.Classes.CivitaiModels;
using Microsoft.EntityFrameworkCore;
using ApiModelVersion = DiffusionNexus.Service.Classes.CivitaiModels.ModelVersion;
using DbModel = DiffusionNexus.Legacy.DataAccess.Entities.Model;
using DbModelVersion = DiffusionNexus.Legacy.DataAccess.Entities.ModelVersion;

namespace DiffusionNexus.Legacy.Service;

public class ModelDataImportService
{
    private readonly DiffusionNexusDbContext _context;

    public ModelDataImportService(DiffusionNexusDbContext context)
    {
        _context = context;
    }

    public async Task<DbModel> ImportFromApiResponseAsync(string jsonResponse, CancellationToken cancellationToken = default)
    {
        var modelData = JsonSerializer.Deserialize<ModelData>(jsonResponse);
        if (modelData == null)
            throw new InvalidOperationException("Failed to deserialize model data");

        var existingModel = await _context.Models
            .Include(m => m.Versions)
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.CivitaiModelId == modelData.Id.ToString(), cancellationToken);

        if (existingModel != null)
        {
            UpdateModel(existingModel, modelData);
            await _context.SaveChangesAsync(cancellationToken);
            return existingModel;
        }

        var model = MapToModel(modelData);
        _context.Models.Add(model);
        await _context.SaveChangesAsync(cancellationToken);
        return model;
    }

    public async Task<DbModel> ImportFromVersionResponseAsync(string versionJsonResponse, CancellationToken cancellationToken = default)
    {
        var versionData = JsonSerializer.Deserialize<ApiModelVersion>(versionJsonResponse);
        if (versionData == null)
            throw new InvalidOperationException("Failed to deserialize version data");

        string modelId = versionData.Id.ToString();
        var existingModel = await _context.Models
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TrainedWords)
            .FirstOrDefaultAsync(m => m.Versions.Any(v => v.CivitaiVersionId == versionData.Id.ToString()), cancellationToken);

        if (existingModel != null)
        {
            var existingVersion = existingModel.Versions.First(v => v.CivitaiVersionId == versionData.Id.ToString());
            UpdateVersion(existingVersion, versionData);
            await _context.SaveChangesAsync(cancellationToken);
            return existingModel;
        }

        var model = new DbModel
        {
            CivitaiModelId = modelId,
            Name = versionData.Name,
            Type = "UNKNOWN",
            CreatedAt = DateTime.UtcNow
        };

        var version = MapToModelVersion(versionData);
        model.Versions.Add(version);

        _context.Models.Add(model);
        await _context.SaveChangesAsync(cancellationToken);
        return model;
    }

    public async Task UpdateLocalFilePathAsync(string sha256Hash, string localFilePath, CancellationToken cancellationToken = default)
    {
        var modelFile = await _context.ModelFiles
            .FirstOrDefaultAsync(f => f.SHA256Hash == sha256Hash, cancellationToken);

        if (modelFile != null)
        {
            modelFile.LocalFilePath = localFilePath;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private DbModel MapToModel(ModelData data)
    {
        var model = new DbModel
        {
            CivitaiModelId = data.Id.ToString(),
            Name = data.Name,
            Description = data.Description,
            Type = data.Type,
            Nsfw = data.Nsfw,
            NsfwLevel = data.NsfwLevel,
            AllowNoCredit = data.AllowNoCredit,
            AllowCommercialUse = data.AllowCommercialUse,
            AllowDerivatives = data.AllowDerivatives,
            UserId = data.UserId,
            CreatorUsername = data.Creator?.Username,
            CreatorImage = data.Creator?.Image,
            CreatedAt = DateTime.UtcNow
        };

        if (data.Tags != null)
        {
            foreach (var tag in data.Tags)
            {
                model.Tags.Add(new ModelTag { Tag = tag });
            }
        }

        if (data.ModelVersions != null)
        {
            foreach (var versionData in data.ModelVersions)
            {
                model.Versions.Add(MapToModelVersion(versionData));
            }
        }

        return model;
    }

    private DbModelVersion MapToModelVersion(ApiModelVersion data)
    {
        var version = new DbModelVersion
        {
            CivitaiVersionId = data.Id.ToString(),
            Name = data.Name,
            Description = data.Description,
            BaseModel = data.BaseModel,
            BaseModelType = data.BaseModelType,
            CreatedAt = data.CreatedAt,
            PublishedAt = data.PublishedAt,
            Status = data.Status,
            NsfwLevel = data.NsfwLevel,
            DownloadUrl = data.DownloadUrl
        };

        if (data.TrainedWords != null)
        {
            foreach (var word in data.TrainedWords)
            {
                version.TrainedWords.Add(new TrainedWord { Word = word });
            }
        }

        if (data.Files != null)
        {
            foreach (var fileData in data.Files)
            {
                version.Files.Add(new ModelFile
                {
                    CivitaiFileId = fileData.Id.ToString(),
                    Name = fileData.Name,
                    Type = fileData.Type,
                    SizeKB = fileData.SizeKB,
                    SHA256Hash = fileData.Hashes?.SHA256,
                    DownloadUrl = fileData.DownloadUrl,
                    IsPrimary = fileData.Primary,
                    PickleScanResult = fileData.PickleScanResult,
                    VirusScanResult = fileData.VirusScanResult,
                    ScannedAt = fileData.ScannedAt
                });
            }
        }

        if (data.Images != null)
        {
            foreach (var imageData in data.Images)
            {
                version.Images.Add(new ModelImage
                {
                    Url = imageData.Url,
                    NsfwLevel = imageData.NsfwLevel,
                    Width = imageData.Width,
                    Height = imageData.Height,
                    Hash = imageData.Hash,
                    Type = imageData.Type,
                    Minor = imageData.Minor,
                    Poi = imageData.Poi
                });
            }
        }

        return version;
    }

    private void UpdateModel(DbModel model, ModelData data)
    {
        model.Name = data.Name;
        model.Description = data.Description;
        model.Type = data.Type;
        model.Nsfw = data.Nsfw;
        model.NsfwLevel = data.NsfwLevel;
        model.UpdatedAt = DateTime.UtcNow;

        _context.ModelTags.RemoveRange(model.Tags);
        model.Tags.Clear();

        if (data.Tags != null)
        {
            foreach (var tag in data.Tags)
            {
                model.Tags.Add(new ModelTag { Tag = tag, ModelId = model.Id });
            }
        }
    }

    private void UpdateVersion(DbModelVersion version, ApiModelVersion data)
    {
        version.Name = data.Name;
        version.Description = data.Description;
        version.BaseModel = data.BaseModel;
        version.DownloadUrl = data.DownloadUrl;

        _context.TrainedWords.RemoveRange(version.TrainedWords);
        version.TrainedWords.Clear();

        if (data.TrainedWords != null)
        {
            foreach (var word in data.TrainedWords)
            {
                version.TrainedWords.Add(new TrainedWord { Word = word, ModelVersionId = version.Id });
            }
        }
    }
}
