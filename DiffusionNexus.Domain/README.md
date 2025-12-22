# DiffusionNexus.Domain

Domain entities for the DiffusionNexus application. Designed for Entity Framework Core.

## Architecture

The domain follows the Civitai data model:

```
Model (1) ?????????< ModelVersion (*)
  ?                       ?
  ?                       ?????< ModelFile (*)
  ?                       ?
  ?                       ?????< ModelImage (*)
  ?                       ?
  ?                       ?????< TriggerWord (*)
  ?
  ?????< Creator (1)
  ?
  ?????<>???< Tag (*) [via ModelTag]
```

## Key Concepts

### One Tile Per Model

The new architecture displays **one tile per Model**, not per ModelVersion:
- A Model represents a single resource (e.g., "Character LoRA")
- A Model can have multiple Versions (v1.0, v2.0, High/Low noise variants)
- Users select which Version to use from within the Model tile

### Data Sources

Models can come from multiple sources:
- **LocalFile**: Discovered by scanning local .safetensors files
- **CivitaiApi**: Fetched from Civitai API (by hash or ID)
- **Manual**: User-entered data

### Entity Framework Ready

Entities are designed for EF Core:
- Integer primary keys (`Id`)
- Navigation properties for relationships
- No EF dependencies yet (added when implementing persistence)

## Image Storage (Hybrid Approach)

ModelImage uses a hybrid storage strategy for optimal performance:

| Data | Storage | Size | Purpose |
|------|---------|------|---------|
| `ThumbnailData` | SQLite BLOB | ~30-80 KB | Instant tile display |
| `LocalCachePath` | File system | Full size | Detail view, zoom |
| `Url` | SQLite TEXT | ~100 bytes | Re-download if needed |
| `BlurHash` | SQLite TEXT | ~30 bytes | Placeholder while loading |

```csharp
// Thumbnail is stored in DB for instant loading
var image = new ModelImage
{
    Url = "https://civitai.com/...?",
    ThumbnailData = thumbnailBytes,      // BLOB in DB
    ThumbnailMimeType = "image/webp",
    ThumbnailWidth = 256,
    ThumbnailHeight = 384,
    LocalCachePath = "ab/abc123.cache",  // Relative path on disk
    IsLocalCacheValid = true
};

// Check what's available
if (image.HasThumbnail)      // Thumbnail in DB
if (image.HasLocalCache)      // Full image on disk
```

### Benefits

1. **Instant tile loading** - thumbnails are in the DB query result
2. **No orphaned files** - cascade delete handles cleanup
3. **Offline support** - works without internet after first cache
4. **Reasonable DB size** - ~50KB × 10 images × 1000 models = ~500MB

## Entities

### Model

The aggregate root. Represents a Civitai model.

```csharp
var model = new Model
{
    CivitaiId = 12345,
    Name = "Character LoRA",
    Type = ModelType.LORA,
    Creator = new Creator { Username = "artist" }
};

// Add versions
model.Versions.Add(new ModelVersion
{
    Name = "v1.0",
    BaseModel = BaseModelType.SDXL10
});
```

### ModelVersion

A specific version of a model.

```csharp
var version = new ModelVersion
{
    Name = "High Noise",
    BaseModel = BaseModelType.WanVideo22,
    DownloadCount = 5000
};

// Add files
version.Files.Add(new ModelFile
{
    FileName = "model_high.safetensors",
    HashSHA256 = "abc123...",
    Format = FileFormat.SafeTensor
});

// Add images with thumbnails
version.Images.Add(new ModelImage
{
    Url = "https://...",
    Width = 512,
    Height = 768,
    ThumbnailData = thumbnailBytes,
    ThumbnailMimeType = "image/webp"
});
```

### ModelFile

A downloadable file with security scan results.

```csharp
var file = new ModelFile
{
    FileName = "model.safetensors",
    SizeKB = 250000,  // ~244 MB
    HashSHA256 = "...",
    LocalPath = @"C:\models\lora\model.safetensors"
};

Console.WriteLine(file.SizeDisplay);  // "244.1 MB"
Console.WriteLine(file.IsSecure);     // true if scans passed
```

## Enums

| Enum | Values |
|------|--------|
| `ModelType` | LORA, Checkpoint, Controlnet, etc. |
| `BaseModelType` | SD15, SDXL10, Flux1D, WanVideo22, etc. |
| `FileFormat` | SafeTensor, PickleTensor, Diffusers |
| `ScanResult` | Pending, Success, Danger, Error |
| `NsfwLevel` | None, Soft, Mature, X |
| `DataSource` | LocalFile, CivitaiApi, Manual |

## Services

### IImageCacheService

Service for downloading and caching preview images:

```csharp
// Download and create thumbnail
var result = await imageCacheService.DownloadAndCacheAsync(
    "https://civitai.com/images/12345.jpeg",
    new ImageCacheOptions
    {
        ThumbnailMaxSize = 256,
        ThumbnailQuality = 85,
        UseWebP = true,
        CacheFullImage = true
    });

if (result.Success)
{
    image.ThumbnailData = result.ThumbnailData;
    image.ThumbnailMimeType = result.ThumbnailMimeType;
    image.LocalCachePath = result.LocalCachePath;
    image.IsLocalCacheValid = true;
}
```

## Future: EF Core Configuration

When implementing persistence:

```csharp
// DbContext configuration (future)
modelBuilder.Entity<Model>(e =>
{
    e.HasKey(m => m.Id);
    e.HasIndex(m => m.CivitaiId).IsUnique();
    e.HasOne(m => m.Creator).WithMany(c => c.Models);
    e.HasMany(m => m.Versions).WithOne(v => v.Model);
});

modelBuilder.Entity<ModelImage>(e =>
{
    e.HasKey(i => i.Id);
    e.Property(i => i.ThumbnailData).HasColumnType("BLOB");
    e.HasIndex(i => new { i.ModelVersionId, i.SortOrder });
    
    // Ignore computed properties
    e.Ignore(i => i.HasThumbnail);
    e.Ignore(i => i.HasLocalCache);
    e.Ignore(i => i.IsPrimary);
});

modelBuilder.Entity<ModelTag>(e =>
{
    e.HasKey(mt => new { mt.ModelId, mt.TagId });
});
