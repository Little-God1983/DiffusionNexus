# DiffusionNexus Core Database

SQLite database for storing model metadata, files, and cached images.

**Project**: `DiffusionNexus.DataAccess`

## Database File

- **Filename**: `Diffusion_Nexus-core.db`
- **Default Location**: `%LOCALAPPDATA%/DiffusionNexus/Data/`
- **Provider**: SQLite via Entity Framework Core 9

## Schema Overview

```
???????????????       ????????????????????       ???????????????
?  Creators   ?       ?      Models      ?       ?    Tags     ?
???????????????       ????????????????????       ???????????????
? Id (PK)     ????????? CreatorId (FK)   ?       ? Id (PK)     ?
? Username    ?       ? Id (PK)          ????????? Name        ?
? AvatarUrl   ?       ? CivitaiId        ?       ? Normalized  ?
? CreatedAt   ?       ? Name             ?       ???????????????
???????????????       ? Description      ?              ?
                      ? Type             ?              ?
                      ? IsNsfw, IsPoi    ?       ???????????????
                      ? Mode, Source     ?       ?  ModelTags  ?
                      ? License fields   ?       ???????????????
                      ? Timestamps       ????????? ModelId(FK) ?
                      ????????????????????       ? TagId (FK)  ?
                               ?                 ???????????????
                               ? 1:N
                               ?
                      ????????????????????
                      ?  ModelVersions   ?
                      ????????????????????
                      ? Id (PK)          ?
                      ? ModelId (FK)     ?
                      ? CivitaiId        ?
                      ? Name             ?
                      ? BaseModel        ?
                      ? DownloadUrl      ?
                      ? Statistics       ?
                      ????????????????????
                               ?
              ???????????????????????????????????
              ? 1:N            ? 1:N            ? 1:N
              ?                ?                ?
      ???????????????  ???????????????  ????????????????
      ? ModelFiles  ?  ? ModelImages ?  ? TriggerWords ?
      ???????????????  ???????????????  ????????????????
      ? Id (PK)     ?  ? Id (PK)     ?  ? Id (PK)      ?
      ? VersionId   ?  ? VersionId   ?  ? VersionId    ?
      ? FileName    ?  ? Url         ?  ? Word         ?
      ? Hashes      ?  ? Thumbnail   ?  ? Order        ?
      ? LocalPath   ?  ? (BLOB)      ?  ????????????????
      ? ScanResults ?  ? LocalCache  ?
      ???????????????  ? GenMetadata ?
                       ???????????????
```

## Tables

### Models

The aggregate root entity representing a Civitai model.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `Id` | INTEGER | No | Primary key (auto-increment) |
| `CivitaiId` | INTEGER | Yes | Civitai model ID (unique when not null) |
| `Name` | TEXT(500) | No | Model name |
| `Description` | TEXT | Yes | HTML description |
| `Type` | TEXT(50) | No | Model type (LORA, Checkpoint, etc.) |
| `IsNsfw` | INTEGER | No | NSFW flag |
| `IsPoi` | INTEGER | No | Person of interest flag |
| `Mode` | TEXT(20) | No | Availability mode |
| `Source` | TEXT(20) | No | Data source (LocalFile, CivitaiApi, Manual) |
| `CreatedAt` | TEXT | No | Local creation timestamp |
| `UpdatedAt` | TEXT | No | Last update timestamp |
| `LastSyncedAt` | TEXT | Yes | Last API sync timestamp |
| `AllowNoCredit` | INTEGER | No | License: no credit required |
| `AllowCommercialUse` | TEXT(20) | No | License: commercial use level |
| `AllowDerivatives` | INTEGER | No | License: derivatives allowed |
| `AllowDifferentLicense` | INTEGER | No | License: different license allowed |
| `CreatorId` | INTEGER | Yes | Foreign key to Creators |

**Indexes:**
- `IX_Models_CivitaiId` (unique, filtered)
- `IX_Models_Name`
- `IX_Models_Type`
- `IX_Models_CreatedAt`
- `IX_Models_CreatorId`

---

### ModelVersions

Specific versions of a model.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `Id` | INTEGER | No | Primary key |
| `CivitaiId` | INTEGER | Yes | Civitai version ID |
| `ModelId` | INTEGER | No | Foreign key to Models |
| `Name` | TEXT(500) | No | Version name |
| `Description` | TEXT | Yes | Version description/changelog |
| `BaseModel` | TEXT(50) | No | Base model type (SD15, SDXL, etc.) |
| `BaseModelRaw` | TEXT(100) | Yes | Original base model string |
| `CreatedAt` | TEXT | No | Creation timestamp |
| `UpdatedAt` | TEXT | Yes | Update timestamp |
| `PublishedAt` | TEXT | Yes | Publish timestamp |
| `DownloadUrl` | TEXT(2000) | Yes | Primary download URL |
| `EarlyAccessDays` | INTEGER | No | Early access period |
| `DownloadCount` | INTEGER | No | Download statistics |
| `RatingCount` | INTEGER | No | Rating count |
| `Rating` | REAL | No | Average rating |
| `ThumbsUpCount` | INTEGER | No | Thumbs up count |
| `ThumbsDownCount` | INTEGER | No | Thumbs down count |

**Indexes:**
- `IX_ModelVersions_CivitaiId` (unique, filtered)
- `IX_ModelVersions_ModelId`
- `IX_ModelVersions_BaseModel`
- `IX_ModelVersions_CreatedAt`

---

### ModelFiles

Downloadable files for each version.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `Id` | INTEGER | No | Primary key |
| `CivitaiId` | INTEGER | Yes | Civitai file ID |
| `ModelVersionId` | INTEGER | No | Foreign key to ModelVersions |
| `FileName` | TEXT(500) | No | File name |
| `SizeKB` | REAL | No | File size in KB |
| `FileType` | TEXT(50) | No | File type (Model, Training Data) |
| `IsPrimary` | INTEGER | No | Is primary file |
| `Format` | TEXT(20) | No | Format (SafeTensor, PickleTensor) |
| `Precision` | TEXT(10) | No | Precision (FP16, FP32, BF16) |
| `SizeType` | TEXT(10) | No | Size type (Full, Pruned) |
| `DownloadUrl` | TEXT(2000) | Yes | Download URL |
| `PickleScanResult` | TEXT(20) | No | Pickle scan status |
| `PickleScanMessage` | TEXT(1000) | Yes | Pickle scan message |
| `VirusScanResult` | TEXT(20) | No | Virus scan status |
| `ScannedAt` | TEXT | Yes | Scan timestamp |
| `HashAutoV1` | TEXT(20) | Yes | AutoV1 hash |
| `HashAutoV2` | TEXT(20) | Yes | AutoV2 hash |
| `HashSHA256` | TEXT(64) | Yes | SHA256 hash |
| `HashCRC32` | TEXT(10) | Yes | CRC32 hash |
| `HashBLAKE3` | TEXT(64) | Yes | BLAKE3 hash |
| `LocalPath` | TEXT(1000) | Yes | Local file path |
| `IsLocalFileValid` | INTEGER | No | Local file valid flag |
| `LocalFileVerifiedAt` | TEXT | Yes | Last verification timestamp |

**Indexes:**
- `IX_ModelFiles_CivitaiId`
- `IX_ModelFiles_ModelVersionId`
- `IX_ModelFiles_HashSHA256`
- `IX_ModelFiles_LocalPath`

---

### ModelImages

Preview images with thumbnail storage.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `Id` | INTEGER | No | Primary key |
| `CivitaiId` | INTEGER | Yes | Civitai image ID |
| `ModelVersionId` | INTEGER | No | Foreign key to ModelVersions |
| `Url` | TEXT(2000) | No | Image URL |
| `IsNsfw` | INTEGER | No | NSFW flag |
| `NsfwLevel` | TEXT(10) | No | NSFW level (None, Soft, Mature, X) |
| `Width` | INTEGER | No | Original width |
| `Height` | INTEGER | No | Original height |
| `BlurHash` | TEXT(100) | Yes | BlurHash for placeholder |
| `SortOrder` | INTEGER | No | Display order (0 = primary) |
| `CreatedAt` | TEXT | Yes | Creation timestamp |
| `PostId` | INTEGER | Yes | Civitai post ID |
| `Username` | TEXT(200) | Yes | Image creator username |
| `ThumbnailData` | **BLOB** | Yes | Thumbnail image data (~30-80KB) |
| `ThumbnailMimeType` | TEXT(50) | Yes | Thumbnail MIME type |
| `ThumbnailWidth` | INTEGER | Yes | Thumbnail width |
| `ThumbnailHeight` | INTEGER | Yes | Thumbnail height |
| `LocalCachePath` | TEXT(500) | Yes | Full image cache path |
| `IsLocalCacheValid` | INTEGER | No | Cache valid flag |
| `CachedAt` | TEXT | Yes | Cache timestamp |
| `CachedFileSize` | INTEGER | Yes | Cached file size |
| `Prompt` | TEXT | Yes | Generation prompt |
| `NegativePrompt` | TEXT | Yes | Negative prompt |
| `Seed` | INTEGER | Yes | Generation seed |
| `Steps` | INTEGER | Yes | Generation steps |
| `Sampler` | TEXT(100) | Yes | Sampler name |
| `CfgScale` | REAL | Yes | CFG scale |
| `GenerationModel` | TEXT(200) | Yes | Model used for generation |
| `DenoisingStrength` | REAL | Yes | Denoising strength |
| `LikeCount` | INTEGER | No | Like count |
| `HeartCount` | INTEGER | No | Heart count |
| `CommentCount` | INTEGER | No | Comment count |

**Indexes:**
- `IX_ModelImages_CivitaiId`
- `IX_ModelImages_ModelVersionId`
- `IX_ModelImages_ModelVersionId_SortOrder`

---

### Creators

Model creators/authors.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `Id` | INTEGER | No | Primary key |
| `Username` | TEXT(200) | No | Unique username |
| `AvatarUrl` | TEXT(2000) | Yes | Avatar image URL |
| `CreatedAt` | TEXT | No | First seen timestamp |

**Indexes:**
- `IX_Creators_Username` (unique)

---

### Tags

Tag definitions.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `Id` | INTEGER | No | Primary key |
| `Name` | TEXT(200) | No | Display name |
| `NormalizedName` | TEXT(200) | No | Lowercase for searching |

**Indexes:**
- `IX_Tags_NormalizedName` (unique)

---

### ModelTags

Many-to-many relationship between Models and Tags.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `ModelId` | INTEGER | No | Foreign key to Models (PK) |
| `TagId` | INTEGER | No | Foreign key to Tags (PK) |

**Indexes:**
- Primary key on `(ModelId, TagId)`
- `IX_ModelTags_TagId`

---

### TriggerWords

Activation words for model versions.

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| `Id` | INTEGER | No | Primary key |
| `ModelVersionId` | INTEGER | No | Foreign key to ModelVersions |
| `Word` | TEXT(500) | No | The trigger word |
| `Order` | INTEGER | No | Display order |

**Indexes:**
- `IX_TriggerWords_ModelVersionId`

---

## Migrations

### Creating Migrations

```bash
cd DiffusionNexus.DataAccess
dotnet ef migrations add <MigrationName> --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
```

### Applying Migrations

```bash
dotnet ef database update --context DiffusionNexusCoreDbContext
```

### Migration History

| Migration | Date | Description |
|-----------|------|-------------|
| `InitialCreate` | 2024-12-22 | Initial schema with all tables |

---

## Usage

### Registration

```csharp
// In Program.cs or Startup.cs
services.AddDiffusionNexusCoreDatabase();

// With custom directory
services.AddDiffusionNexusCoreDatabase(@"D:\MyData");

// Using factory pattern (for scoped usage)
services.AddDiffusionNexusCoreDatabaseFactory();
```

### Querying

```csharp
// Get model with all related data
var model = await db.Models
    .Include(m => m.Creator)
    .Include(m => m.Tags).ThenInclude(mt => mt.Tag)
    .Include(m => m.Versions)
        .ThenInclude(v => v.Files)
    .Include(m => m.Versions)
        .ThenInclude(v => v.Images)
    .FirstOrDefaultAsync(m => m.Id == id);

// Find by SHA256 hash
var file = await db.ModelFiles
    .Include(f => f.ModelVersion)
        .ThenInclude(v => v.Model)
    .FirstOrDefaultAsync(f => f.HashSHA256 == hash);

// Get models by type
var loras = await db.Models
    .Where(m => m.Type == ModelType.LORA)
    .OrderByDescending(m => m.CreatedAt)
    .ToListAsync();
```

### Image Thumbnails

```csharp
// Thumbnails are stored as BLOBs for instant loading
var images = await db.ModelImages
    .Where(i => i.ModelVersionId == versionId)
    .OrderBy(i => i.SortOrder)
    .Select(i => new {
        i.Id,
        i.ThumbnailData,      // BLOB - ready to display
        i.ThumbnailMimeType,
        i.Width,
        i.Height
    })
    .ToListAsync();
```

---

## Storage Estimates

| Content | Per Item | 1,000 Models | 10,000 Models |
|---------|----------|--------------|---------------|
| Model metadata | ~2 KB | ~2 MB | ~20 MB |
| 5 versions each | ~1 KB | ~5 MB | ~50 MB |
| 2 files per version | ~500 B | ~5 MB | ~50 MB |
| 5 images per version (thumbnails) | ~50 KB | ~250 MB | ~2.5 GB |
| **Total estimate** | | ~262 MB | ~2.6 GB |

> **Note**: Thumbnail BLOBs are the largest contributor to database size. Consider periodic cleanup of unused thumbnails.
