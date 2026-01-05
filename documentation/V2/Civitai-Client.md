# DiffusionNexus Civitai Client

Typed HTTP client for the Civitai REST API.

**Project**: `DiffusionNexus.Civitai`

## Features

- **Strongly-typed DTOs** matching Civitai API responses
- **Async/await** with cancellation token support
- **Dependency injection** friendly via `ICivitaiClient` interface
- **Testable** - mock the interface for unit tests

## Usage

### Basic Usage

```csharp
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;

// Create client
using var client = new CivitaiClient();

// Get LORA models
var response = await client.GetModelsAsync(new CivitaiModelsQuery
{
    Types = [CivitaiModelType.LORA],
    Limit = 20,
    Sort = CivitaiModelSort.MostDownloaded
});

foreach (var model in response.Items)
{
    Console.WriteLine($"{model.Name} by {model.Creator?.Username}");
    foreach (var version in model.ModelVersions)
    {
        Console.WriteLine($"  - {version.Name} ({version.BaseModel})");
    }
}
```

### Get Model by Hash (Local File Lookup)

```csharp
// Look up a local .safetensors file on Civitai
var sha256 = ComputeSHA256(filePath);
var version = await client.GetModelVersionByHashAsync(sha256);

if (version != null)
{
    Console.WriteLine($"Found: {version.Model?.Name} - {version.Name}");
}
```

### With API Key (Authenticated Requests)

```csharp
var apiKey = "your-api-key";
var model = await client.GetModelAsync(12345, apiKey);
```

### Dependency Injection

```csharp
// In Startup/Program.cs
services.AddHttpClient<ICivitaiClient, CivitaiClient>(client =>
{
    client.BaseAddress = new Uri("https://civitai.com/api/v1/");
});
```

## API Coverage

| Endpoint | Method |
|----------|--------|
| GET /api/v1/models | `GetModelsAsync()` |
| GET /api/v1/models/:id | `GetModelAsync()` |
| GET /api/v1/model-versions/:id | `GetModelVersionAsync()` |
| GET /api/v1/model-versions/by-hash/:hash | `GetModelVersionByHashAsync()` |
| GET /api/v1/images | `GetImagesAsync()` |
| GET /api/v1/tags | `GetTagsAsync()` |
| GET /api/v1/creators | `GetCreatorsAsync()` |

## DTOs

| Class | Description |
|-------|-------------|
| `CivitaiModel` | A model with multiple versions |
| `CivitaiModelVersion` | A specific version with files and images |
| `CivitaiModelFile` | A downloadable file with hashes |
| `CivitaiModelImage` | A preview image with generation metadata |
| `CivitaiCreator` | Model creator information |
| `CivitaiPagedResponse<T>` | Paginated response wrapper |

## Query Parameters

### CivitaiModelsQuery

```csharp
new CivitaiModelsQuery
{
    Limit = 20,                           // 1-100
    Page = 1,
    Query = "character",                  // Search by name
    Tag = "anime",                        // Filter by tag
    Username = "creator",                 // Filter by creator
    Types = [CivitaiModelType.LORA],     // Filter by type
    Sort = CivitaiModelSort.Newest,
    Period = CivitaiPeriod.Month,
    Nsfw = false,
    BaseModel = CivitaiBaseModel.SDXL10
}
```

### CivitaiImagesQuery

```csharp
new CivitaiImagesQuery
{
    Limit = 100,
    ModelId = 12345,
    ModelVersionId = 67890,
    Nsfw = false,
    Sort = "Newest"
}
```

## Enums

### CivitaiModelType

```csharp
public enum CivitaiModelType
{
    Checkpoint,
    TextualInversion,
    Hypernetwork,
    AestheticGradient,
    LORA,
    LoCon,
    DoRA,
    Controlnet,
    Poses,
    Upscaler,
    MotionModule,
    VAE,
    Wildcards,
    Workflows,
    Other
}
```

### CivitaiBaseModel Constants

```csharp
public static class CivitaiBaseModel
{
    public const string SD15 = "SD 1.5";
    public const string SDXL10 = "SDXL 1.0";
    public const string Flux1D = "Flux.1 D";
    public const string WanVideo22 = "Wan Video 2.2";
    // ... and more
}
```

## Error Handling

```csharp
try
{
    var model = await client.GetModelAsync(12345);
}
catch (HttpRequestException ex)
{
    // Network or HTTP error
    Console.WriteLine($"Request failed: {ex.Message}");
}
catch (JsonException ex)
{
    // Unexpected response format
    Console.WriteLine($"Parse failed: {ex.Message}");
}
```

## Rate Limiting

The Civitai API has rate limits. Consider:
- Adding delays between requests
- Caching responses
- Using the API key for higher limits

```csharp
// Simple delay between requests
foreach (var modelId in modelIds)
{
    var model = await client.GetModelAsync(modelId);
    await Task.Delay(100); // 100ms delay
}
```
