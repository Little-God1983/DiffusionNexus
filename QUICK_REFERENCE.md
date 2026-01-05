# Quick Reference: Common Database Operations

## Setup & Initialization

```csharp
// Initialize database (do once on app startup)
await DbContextFactory.EnsureDatabaseCreatedAsync();

// Create context
using var context = DbContextFactory.CreateDbContext();

// Custom database path
using var context = DbContextFactory.CreateDbContext(@"C:\MyApp\models.db");
```

## Import Data

```csharp
// Import directory
var importService = new LocalFileImportService(context, apiClient);
await importService.ImportDirectoryAsync(@"C:\Models\Lora", progress);

// Import single file
await importService.ImportFileAsync(@"C:\Models\my_lora.safetensors", progress);

// Sync file (fetch from API if needed)
var syncService = new ModelSyncService(context, apiClient);
var model = await syncService.SyncLocalFileAsync(filePath, sha256Hash, progress);
```

## Query Models

```csharp
// Get all models
var allModels = await context.Models.ToListAsync();

// Get models with versions and files
var models = await context.Models
    .Include(m => m.Versions)
        .ThenInclude(v => v.Files)
    .Include(m => m.Tags)
    .ToListAsync();

// Get by Civitai ID
var model = await context.Models
    .FirstOrDefaultAsync(m => m.CivitaiModelId == "12345");

// Get by type
var loras = await context.Models
    .Where(m => m.Type == "LORA")
    .ToListAsync();

// Get NSFW models
var nsfwModels = await context.Models
    .Where(m => m.Nsfw)
    .ToListAsync();

// Get by tag
var characterModels = await context.Models
    .Where(m => m.Tags.Any(t => t.Tag == "character"))
    .ToListAsync();

// Search by name
var searchResults = await context.Models
    .Where(m => m.Name.Contains(searchTerm))
    .ToListAsync();
```

## Query Files

```csharp
// Get file by hash
var fileRepo = new ModelFileRepository(context);
var file = await fileRepo.GetBySHA256HashAsync(sha256);

// Get file by local path
var file = await fileRepo.GetByLocalFilePathAsync(@"C:\Models\my_lora.safetensors");

// Get all local files
var localFiles = await fileRepo.GetLocalFilesAsync();

// Get files without local path (not downloaded)
var remoteFiles = await context.ModelFiles
    .Where(f => f.LocalFilePath == null)
    .ToListAsync();

// Get primary file for a version
var primaryFile = await context.ModelFiles
    .FirstOrDefaultAsync(f => f.ModelVersionId == versionId && f.IsPrimary);
```

## Query Versions

```csharp
// Get versions for a model
var versions = await context.ModelVersions
    .Where(v => v.ModelId == modelId)
    .Include(v => v.Files)
    .Include(v => v.Images)
    .ToListAsync();

// Get by base model
var sdxlVersions = await context.ModelVersions
    .Where(v => v.BaseModel.Contains("SDXL"))
    .ToListAsync();

// Get latest version
var latestVersion = await context.ModelVersions
    .Where(v => v.ModelId == modelId)
    .OrderByDescending(v => v.PublishedAt)
    .FirstOrDefaultAsync();
```

## Update Operations

```csharp
// Update local file path
var file = await context.ModelFiles.FindAsync(fileId);
file.LocalFilePath = newPath;
await context.SaveChangesAsync();

// Update model metadata
var model = await context.Models.FindAsync(modelId);
model.Name = newName;
model.Description = newDescription;
await context.SaveChangesAsync();

// Add tag
var model = await context.Models
    .Include(m => m.Tags)
    .FirstAsync(m => m.Id == modelId);
model.Tags.Add(new ModelTag { Tag = "new_tag" });
await context.SaveChangesAsync();
```

## Delete Operations

```csharp
// Delete model (cascades to versions, files, etc.)
var model = await context.Models.FindAsync(modelId);
context.Models.Remove(model);
await context.SaveChangesAsync();

// Delete file
var file = await context.ModelFiles.FindAsync(fileId);
context.ModelFiles.Remove(file);
await context.SaveChangesAsync();

// Delete orphaned versions (no local files)
var orphanedVersions = await context.ModelVersions
    .Where(v => !v.Files.Any(f => f.LocalFilePath != null))
    .ToListAsync();
context.ModelVersions.RemoveRange(orphanedVersions);
await context.SaveChangesAsync();
```

## Statistics Queries

```csharp
// Count models by type
var typeStats = await context.Models
    .GroupBy(m => m.Type)
    .Select(g => new { Type = g.Key, Count = g.Count() })
    .ToListAsync();

// Count files by base model
var baseModelStats = await context.ModelVersions
    .GroupBy(v => v.BaseModel)
    .Select(g => new { BaseModel = g.Key, Count = g.Count() })
    .ToListAsync();

// Total storage size
var totalSize = await context.ModelFiles
    .SumAsync(f => f.SizeKB);

// Models with trained words
var modelsWithWords = await context.ModelVersions
    .Where(v => v.TrainedWords.Any())
    .CountAsync();
```

## Metadata Provider Usage

```csharp
// Setup provider chain
var providers = new IModelMetadataProvider[]
{
    new DatabaseMetadataProvider(context),
    new LocalFileMetadataProvider(),
    new CivitaiApiMetadataProvider(apiClient, apiKey)
};

// Get metadata (tries DB first, then files, then API)
var modelClass = new ModelClass();
foreach (var provider in providers)
{
    if (await provider.CanHandleAsync(filePath))
    {
        modelClass = await provider.GetModelMetadataAsync(filePath);
        if (modelClass.HasFullMetadata) break;
    }
}
```

## Performance Tips

```csharp
// Use AsNoTracking for read-only queries
var models = await context.Models
    .AsNoTracking()
    .ToListAsync();

// Project to DTOs to avoid loading unnecessary data
var modelNames = await context.Models
    .Select(m => new { m.Id, m.Name })
    .ToListAsync();

// Use pagination for large result sets
var page = await context.Models
    .Skip(pageNumber * pageSize)
    .Take(pageSize)
    .ToListAsync();

// Cache frequently accessed data
private static List<Model>? _cachedModels;
if (_cachedModels == null)
{
    _cachedModels = await context.Models
        .AsNoTracking()
        .ToListAsync();
}
```

## Transaction Example

```csharp
using var transaction = await context.Database.BeginTransactionAsync();
try
{
    // Multiple operations
    var model = new Model { ... };
    context.Models.Add(model);
    await context.SaveChangesAsync();
    
    var version = new ModelVersion { ModelId = model.Id, ... };
    context.ModelVersions.Add(version);
    await context.SaveChangesAsync();
    
    await transaction.CommitAsync();
}
catch
{
    await transaction.RollbackAsync();
    throw;
}
```

## Maintenance

```csharp
// Vacuum database (optimize)
await context.Database.ExecuteSqlRawAsync("VACUUM;");

// Check database integrity
var result = await context.Database
    .SqlQueryRaw<string>("PRAGMA integrity_check;")
    .ToListAsync();

// Get database file size
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DiffusionNexus", "diffusion_nexus.db");
var fileInfo = new FileInfo(dbPath);
var sizeInMB = fileInfo.Length / 1024.0 / 1024.0;
```

## Common Patterns

### Find or Create
```csharp
var model = await context.Models
    .FirstOrDefaultAsync(m => m.CivitaiModelId == civitaiId);

if (model == null)
{
    model = new Model { CivitaiModelId = civitaiId, ... };
    context.Models.Add(model);
    await context.SaveChangesAsync();
}
```

### Bulk Insert
```csharp
var models = new List<Model>();
// ... populate list

context.Models.AddRange(models);
await context.SaveChangesAsync();
```

### Conditional Update
```csharp
var file = await context.ModelFiles.FindAsync(fileId);
if (file != null && file.LocalFilePath != newPath)
{
    file.LocalFilePath = newPath;
    await context.SaveChangesAsync();
}
```
