# Migration Guide: From In-Memory to SQLite Database

## Overview

This guide helps you migrate from the previous in-memory `ModelClass` system to the new SQLite database-backed approach.

## Key Changes

### 1. Data Storage Location

**Before:**
- Models stored in `List<ModelClass>` in memory
- Data lost when application closes
- Required re-scanning files each time

**After:**
- Models stored in SQLite database
- Data persists between sessions
- Only scan new/changed files

### 2. Model Representation

**Before:**
```csharp
public class ModelClass
{
    public string ModelId { get; set; }
    public string SafeTensorFileName { get; set; }
    public string DiffusionBaseModel { get; set; }
    public List<FileInfo> AssociatedFilesInfo { get; set; }
    // ... etc
}
```

**After:**
```csharp
// Database entities (in DiffusionNexus.DataAccess.Entities)
public class Model { /* Civitai model info */ }
public class ModelVersion { /* Version-specific info */ }
public class ModelFile { /* File metadata + local path */ }

// Legacy ModelClass still exists for compatibility
// Can be populated from database via DatabaseMetadataProvider
```

### 3. Metadata Retrieval

**Before:**
```csharp
var service = new JsonInfoFileReaderService(basePath, metadataFetcher);
var models = await service.GetModelData(progress, cancellationToken);

// Result: List<ModelClass> with data from files/API
```

**After:**
```csharp
// Option 1: Import files to database
var importService = new LocalFileImportService(context, apiClient);
await importService.ImportDirectoryAsync(basePath, progress);

// Option 2: Query database
var syncService = new ModelSyncService(context, apiClient);
var models = await syncService.GetAllModelsAsync();

// Option 3: Use existing code with DatabaseMetadataProvider
var providers = new IModelMetadataProvider[]
{
    new DatabaseMetadataProvider(context),
    new LocalFileMetadataProvider(),
    new CivitaiApiMetadataProvider(apiClient, apiKey)
};
var fileController = new FileControllerService(providers);
```

## Step-by-Step Migration

### Step 1: Initialize Database

Add to your application startup:

```csharp
// App startup
await DbContextFactory.EnsureDatabaseCreatedAsync();
```

### Step 2: Update Service Initialization

**Before:**
```csharp
var metadataService = new CivitaiMetaDataService(apiKey);
var fileController = new FileControllerService(
    new LocalFileMetadataProvider(),
    new CivitaiApiMetadataProvider(apiClient, apiKey)
);
```

**After:**
```csharp
var context = DbContextFactory.CreateDbContext();
var metadataService = new CivitaiMetaDataService(apiKey);
var fileController = new FileControllerService(
    new DatabaseMetadataProvider(context),      // Add this first
    new LocalFileMetadataProvider(),
    new CivitaiApiMetadataProvider(apiClient, apiKey)
);
```

### Step 3: First-Time Import

Run this once to populate the database:

```csharp
using var context = DbContextFactory.CreateDbContext();
var apiClient = new CivitaiApiClient(new HttpClient());
var importService = new LocalFileImportService(context, apiClient, apiKey);

var progress = new Progress<ProgressReport>(r => 
{
    logger.Log(r.LogLevel, r.StatusMessage);
});

await importService.ImportDirectoryAsync(loraDirectory, progress);
```

### Step 4: Update File Processing Logic

**Before:**
```csharp
var models = await jsonReader.GetModelData(progress, token);
foreach (var model in models)
{
    // Process model
    ProcessModel(model);
}
```

**After - Option A (Database-first):**
```csharp
// Import/sync files first
await importService.ImportDirectoryAsync(directory, progress, token);

// Query database
using var context = DbContextFactory.CreateDbContext();
var dbModels = await context.Models
    .Include(m => m.Versions)
        .ThenInclude(v => v.Files)
    .ToListAsync(token);

foreach (var dbModel in dbModels)
{
    // Convert to ModelClass if needed for existing code
    var modelClass = await ConvertToModelClass(dbModel);
    ProcessModel(modelClass);
}
```

**After - Option B (Hybrid approach):**
```csharp
// Existing code mostly unchanged, just add DatabaseMetadataProvider
var models = await jsonReader.GetModelData(progress, token);
// DatabaseMetadataProvider will populate from DB when available
// Falls back to files/API for new models
foreach (var model in models)
{
    ProcessModel(model);
}
```

### Step 5: Handle File Moves/Renames

**New capability:**
```csharp
using var context = DbContextFactory.CreateDbContext();
var fileRepo = new ModelFileRepository(context);

// Find by hash (works even if file moved)
var file = await fileRepo.GetBySHA256HashAsync(hash);

// Update path
if (file != null && file.LocalFilePath != newPath)
{
    file.LocalFilePath = newPath;
    await context.SaveChangesAsync();
}
```

## Common Scenarios

### Scenario 1: Display All Local Models

**Before:**
```csharp
var models = await jsonReader.GetModelData(progress, token);
modelGrid.ItemsSource = models;
```

**After:**
```csharp
using var context = DbContextFactory.CreateDbContext();
var localFiles = await context.ModelFiles
    .Where(f => f.LocalFilePath != null)
    .Include(f => f.ModelVersion)
        .ThenInclude(v => v.Model)
    .Include(f => f.ModelVersion)
        .ThenInclude(v => v.TrainedWords)
    .ToListAsync();

// Convert to ModelClass for grid if needed
var models = localFiles.Select(f => new ModelClass
{
    ModelId = f.ModelVersion.Model.CivitaiModelId,
    ModelVersionName = f.ModelVersion.Name,
    DiffusionBaseModel = f.ModelVersion.BaseModel,
    SafeTensorFileName = Path.GetFileNameWithoutExtension(f.LocalFilePath),
    TrainedWords = f.ModelVersion.TrainedWords.Select(w => w.Word).ToList(),
    // ... map other properties
}).ToList();

modelGrid.ItemsSource = models;
```

### Scenario 2: Search by Tag

**Before:**
```csharp
var characterModels = models.Where(m => 
    m.Tags.Any(t => t.Equals("character", StringComparison.OrdinalIgnoreCase))
).ToList();
```

**After:**
```csharp
using var context = DbContextFactory.CreateDbContext();
var characterModels = await context.Models
    .Where(m => m.Tags.Any(t => t.Tag.ToLower() == "character"))
    .Include(m => m.Versions)
        .ThenInclude(v => v.Files.Where(f => f.LocalFilePath != null))
    .ToListAsync();
```

### Scenario 3: Check if Model Exists

**Before:**
```csharp
var exists = models.Any(m => m.ModelId == "12345");
```

**After:**
```csharp
using var context = DbContextFactory.CreateDbContext();
var exists = await context.Models
    .AnyAsync(m => m.CivitaiModelId == "12345");
```

### Scenario 4: Download Missing Files

**Before:**
```csharp
foreach (var model in models.Where(m => m.NoMetaData))
{
    await downloadService.DownloadMetadata(model);
}
```

**After:**
```csharp
using var context = DbContextFactory.CreateDbContext();

// Find files without local paths
var missingFiles = await context.ModelFiles
    .Where(f => f.LocalFilePath == null)
    .Include(f => f.ModelVersion)
    .ToListAsync();

foreach (var file in missingFiles)
{
    if (file.DownloadUrl != null)
    {
        var localPath = await downloadService.DownloadFile(file.DownloadUrl);
        file.LocalFilePath = localPath;
    }
}

await context.SaveChangesAsync();
```

## Backward Compatibility

The `ModelClass` is still used throughout the codebase. The new system provides:

1. **DatabaseMetadataProvider** - Populates `ModelClass` from database
2. **Conversion utilities** - Convert between database entities and `ModelClass`
3. **Existing interfaces** - `IModelMetadataProvider` still works as before

This allows gradual migration without breaking existing code.

## Performance Considerations

### Database Queries

Use `Include()` to load related data efficiently:

```csharp
// Good: One query
var models = await context.Models
    .Include(m => m.Versions)
        .ThenInclude(v => v.Files)
    .Include(m => m.Tags)
    .ToListAsync();

// Bad: N+1 queries
var models = await context.Models.ToListAsync();
foreach (var model in models)
{
    var versions = await context.ModelVersions
        .Where(v => v.ModelId == model.Id)
        .ToListAsync(); // Separate query for each model!
}
```

### Caching

Consider caching frequently accessed data:

```csharp
// Cache all models on app startup
private static List<Model>? _cachedModels;

public static async Task<List<Model>> GetModelsAsync()
{
    if (_cachedModels == null)
    {
        using var context = DbContextFactory.CreateDbContext();
        _cachedModels = await context.Models
            .Include(m => m.Versions)
            .Include(m => m.Tags)
            .ToListAsync();
    }
    return _cachedModels;
}
```

## Troubleshooting

### "Database is locked"
- Ensure you're disposing contexts: `using var context = ...`
- Don't share context instances across threads

### "No such table"
- Run `await DbContextFactory.EnsureDatabaseCreatedAsync()` first

### Slow queries
- Add indexes (already configured for common queries)
- Use `.AsNoTracking()` for read-only queries
- Limit Include() depth

### Migration errors
- Delete the database and recreate if in development
- Production: Create proper migrations using EF Core tools

## Next Steps

1. Initialize database in your app startup
2. Run one-time import of existing files
3. Update UI to query database instead of in-memory lists
4. Remove/reduce file rescanning on each app launch
5. Add features like search, filtering, statistics using database queries
