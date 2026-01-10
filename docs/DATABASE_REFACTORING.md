# Database Refactoring - SQLite with Entity Framework Core

## Overview

This refactoring introduces a SQLite database using Entity Framework Core to store model metadata, replacing the previous in-memory or file-based approach.

## Database Schema

### Core Entities

#### Model
- Represents a Civitai model
- Stores: ID, name, description, type, NSFW info, creator details
- Has many: Versions, Tags

#### ModelVersion
- Represents a specific version of a model
- Stores: Version ID, name, base model, trained words
- Belongs to: Model
- Has many: Files, Images, TrainedWords

#### ModelFile
- Represents a downloadable file for a version
- Stores: File metadata, hashes, download URLs
- **Important**: `LocalFilePath` stores the location of the file on disk
- `SHA256Hash` is indexed for fast lookups

#### ModelImage
- Preview images/videos for a version
- Stores: URL, dimensions, NSFW level
- `LocalFilePath` can store downloaded thumbnails

#### ModelTag
- Tags associated with models
- Used for categorization

#### TrainedWord
- Activation words for the model

## Key Services

### ModelDataImportService
Imports data from Civitai API responses into the database.

```csharp
// Import from full model API response
var model = await importService.ImportFromApiResponseAsync(jsonResponse);

// Import from version-by-hash API response
var model = await importService.ImportFromVersionResponseAsync(versionJson);

// Update local file path
await importService.UpdateLocalFilePathAsync(sha256Hash, filePath);
```

### ModelSyncService
Synchronizes local files with the database.

```csharp
var syncService = new ModelSyncService(context, apiClient);

// Sync a single file
var model = await syncService.SyncLocalFileAsync(filePath, sha256Hash, progress);

// Get all models from database
var models = await syncService.GetAllModelsAsync();

// Get files that exist locally
var localFiles = await syncService.GetLocalFilesAsync();
```

### LocalFileImportService
Scans directories and imports model files.

```csharp
var importService = new LocalFileImportService(context, apiClient, apiKey);

// Import entire directory
await importService.ImportDirectoryAsync(directoryPath, progress);

// Import single file
await importService.ImportFileAsync(filePath, progress);
```

### DatabaseMetadataProvider
Implements `IModelMetadataProvider` to retrieve metadata from the database.

```csharp
var provider = new DatabaseMetadataProvider(context);
var modelClass = await provider.GetModelMetadataAsync(filePath);
```

## Setup and Usage

### 1. Initialize Database

```csharp
using DiffusionNexus.DataAccess;

// Create database with default location
await DbContextFactory.EnsureDatabaseCreatedAsync();

// Or specify custom location
var dbPath = @"C:\MyApp\data.db";
await DbContextFactory.EnsureDatabaseCreatedAsync(dbPath);
```

### 2. Create DbContext

```csharp
// Default location: %LOCALAPPDATA%\DiffusionNexus\diffusion_nexus.db
using var context = DbContextFactory.CreateDbContext();

// Custom location
using var context = DbContextFactory.CreateDbContext(@"C:\MyApp\data.db");
```

### 3. Import Local Files

```csharp
using var context = DbContextFactory.CreateDbContext();
var apiClient = new CivitaiApiClient(new HttpClient());
var importService = new LocalFileImportService(context, apiClient);

var progress = new Progress<ProgressReport>(report =>
{
    Console.WriteLine($"{report.Percentage}% - {report.StatusMessage}");
});

await importService.ImportDirectoryAsync(@"C:\Models\Lora", progress);
```

### 4. Query Models

```csharp
using var context = DbContextFactory.CreateDbContext();

// Get all models with relationships
var models = await context.Models
    .Include(m => m.Versions)
        .ThenInclude(v => v.Files)
    .Include(m => m.Tags)
    .ToListAsync();

// Find model by Civitai ID
var model = await context.Models
    .Include(m => m.Versions)
    .FirstOrDefaultAsync(m => m.CivitaiModelId == "12345");

// Find file by local path
var file = await context.ModelFiles
    .Include(f => f.ModelVersion)
        .ThenInclude(v => v.Model)
    .FirstOrDefaultAsync(f => f.LocalFilePath == @"C:\Models\mymodel.safetensors");

// Find file by SHA256 hash
var file = await context.ModelFiles
    .Include(f => f.ModelVersion)
    .FirstOrDefaultAsync(f => f.SHA256Hash == "abc123...");
```

## Integration with Existing Code

### Using DatabaseMetadataProvider

The `DatabaseMetadataProvider` can be added to the metadata provider chain:

```csharp
var context = DbContextFactory.CreateDbContext();
var providers = new IModelMetadataProvider[]
{
    new DatabaseMetadataProvider(context),           // Check DB first
    new LocalFileMetadataProvider(),                 // Then .civitai.info/.json files
    new CivitaiApiMetadataProvider(apiClient, apiKey) // Finally API
};

var fileController = new FileControllerService(providers);
```

### Data Flow

1. **Initial Import**:
   - Scan directory for model files
   - Check for `.civitai.info` or `.json` files
   - If found, import metadata to database
   - If not, query Civitai API and store results
   - Update `LocalFilePath` in `ModelFile` entity

2. **Subsequent Access**:
   - `DatabaseMetadataProvider` checks hash against database
   - If found, returns metadata from database (fast)
   - If not found, falls back to file/API providers

3. **File Movement**:
   - Hash remains the same
   - Update `LocalFilePath` when file is moved
   - Database maintains relationship

## Migration Notes

### What's Stored vs. What's Not

**Stored in Database:**
- Model metadata (name, description, type, NSFW status)
- Version information (base model, trained words)
- Download URLs for files and images
- SHA256 hashes
- Local file paths (where files are stored on disk)
- Tags and creator info

**Not Stored in Database:**
- Actual model files (safetensors, etc.) - stored on disk
- Preview images/videos - URLs stored, optionally download and store path
- Large description HTML - can be excluded or truncated

### Benefits

1. **Performance**: Fast lookups by hash or local path
2. **Persistence**: Metadata survives between app sessions
3. **Relationships**: Easy to navigate model ? versions ? files
4. **Queries**: Complex filtering and searching
5. **Updates**: Track changes to model metadata over time

### Database Location

Default: `%LOCALAPPDATA%\DiffusionNexus\diffusion_nexus.db`

This can be changed by passing a custom path to `DbContextFactory.CreateDbContext()`.

## Future Enhancements

- [ ] Add migrations for schema changes
- [ ] Implement soft deletes
- [ ] Add audit fields (created/updated timestamps)
- [ ] Cache downloaded preview images
- [ ] Track file moves/renames
- [ ] Add full-text search on descriptions
- [ ] Sync database between devices
- [ ] Export/import database

## Testing

See `DiffusionNexus.Tests` project for examples of:
- Repository usage
- Service integration
- Import scenarios
