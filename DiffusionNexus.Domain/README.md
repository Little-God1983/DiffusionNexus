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

// Add images
version.Images.Add(new ModelImage
{
    Url = "https://...",
    Width = 512,
    Height = 768,
    Prompt = "a beautiful landscape"
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

modelBuilder.Entity<ModelVersion>(e =>
{
    e.HasMany(v => v.Files).WithOne(f => f.ModelVersion);
    e.HasMany(v => v.Images).WithOne(i => i.ModelVersion);
});

modelBuilder.Entity<ModelTag>(e =>
{
    e.HasKey(mt => new { mt.ModelId, mt.TagId });
});
```
