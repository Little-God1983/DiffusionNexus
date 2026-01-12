# DiffusionNexus Captioning Service

Local AI image captioning using vision-language models with LlamaSharp and CUDA 12 GPU acceleration.

**Project**: `DiffusionNexus.Captioning`

## Overview

The Captioning service enables automatic image caption generation using local vision-language models. It runs entirely on your machine using NVIDIA GPU acceleration, ensuring privacy and avoiding cloud API costs.

## Architecture

```
???????????????????????????????????????????????????????????????????
?                     ICaptioningService                          ?
?  ????????????????????????????????????????????????????????????  ?
?  ?                   CaptioningService                       ?  ?
?  ?  ??????????????????  ???????????????????                 ?  ?
?  ?  ? ModelManager   ?  ? ImagePreprocessor?                 ?  ?
?  ?  ? ?????????????  ?  ? ?????????????????                 ?  ?
?  ?  ? • Download     ?  ? • Validate      ?                 ?  ?
?  ?  ? • Status check ?  ? • Resize (2048) ?                 ?  ?
?  ?  ? • File mgmt    ?  ? • Format encode ?                 ?  ?
?  ?  ??????????????????  ???????????????????                 ?  ?
?  ?                           ?                               ?  ?
?  ?  ?????????????????????????????????????????????????????   ?  ?
?  ?  ?              LlamaSharp + CUDA 12                  ?   ?  ?
?  ?  ?  ????????????????  ????????????????????????????   ?   ?  ?
?  ?  ?  ? LLamaWeights ?  ? LLavaWeights (CLIP)      ?   ?   ?  ?
?  ?  ?  ? (GGUF model) ?  ? (Vision encoder)         ?   ?   ?  ?
?  ?  ?  ????????????????  ????????????????????????????   ?   ?  ?
?  ?  ??????????????????????????????????????????????????????   ?  ?
?  ????????????????????????????????????????????????????????????  ?
???????????????????????????????????????????????????????????????????
```

## Supported Models

| Model | Size | VRAM | Quality | Use Case |
|-------|------|------|---------|----------|
| **Qwen 3 VL 8B** | ~5.5 GB | ~8 GB | ????? | Recommended - Latest and most capable |
| **Qwen 2.5 VL 7B** | ~5 GB | ~8 GB | ????? | Good alternative with proven stability |
| **LLaVA v1.6 34B** | ~20 GB | ~24 GB | ????? | Maximum quality, requires high-end GPU |

### Model Details

#### Qwen 3 VL 8B (Q4_K_M) - Recommended
- **Architecture**: Qwen3-VL (Latest)
- **Prompt Format**: ChatML (`<|im_start|>`, `<|im_end|>`)
- **GGUF Source**: [Qwen/Qwen3-VL-8B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF)
- **Key Features**:
  - 256K native context (expandable to 1M)
  - Visual Agent capabilities (operates PC/mobile GUIs)
  - 3D grounding for spatial reasoning
  - 32-language OCR support
  - Hours-long video understanding
  - Enhanced STEM/Math reasoning
- **Best for**: Most users - best balance of quality, features, and resource usage

#### Qwen 2.5 VL 7B (Q4_K_M)
- **Architecture**: Qwen2.5-VL
- **Prompt Format**: ChatML (`<|im_start|>`, `<|im_end|>`)
- **GGUF Source**: [bartowski/Qwen2.5-VL-7B-Instruct-GGUF](https://huggingface.co/bartowski/Qwen2.5-VL-7B-Instruct-GGUF)
- **Best for**: Proven stability, fallback option if Qwen 3 has compatibility issues

#### LLaVA v1.6 34B (Q4_K_M)
- **Architecture**: LLaVA-NeXT (34B parameter)
- **Prompt Format**: Vicuna (`USER:`, `ASSISTANT:`)
- **GGUF Source**: [cjpais/llava-v1.6-34b-gguf](https://huggingface.co/cjpais/llava-v1.6-34b-gguf)
- **Best for**: Highest quality captions when you have 24GB+ VRAM

## Requirements

### Hardware
- **GPU**: NVIDIA GPU with CUDA support (Required)
- **VRAM**: 8+ GB recommended (16+ GB for LLaVA 34B)
- **Disk**: 5-25 GB for model files
- **CUDA**: 12.0+ (supports CUDA 12.8 for RTX 50XX series)

### Software
- Windows 10/11 x64
- NVIDIA GPU drivers (latest recommended)
- .NET 10 Runtime

## Installation

The Captioning project is included in the DiffusionNexus solution. Models are downloaded on-demand.

### NuGet Dependencies

```xml
<PackageReference Include="LLamaSharp" Version="0.21.0" />
<PackageReference Include="LLamaSharp.Backend.Cuda12" Version="0.21.0" />
<PackageReference Include="SkiaSharp" Version="3.119.1" />
```

### Model Storage Location

Models are stored in:
```
%LocalAppData%\DiffusionNexus\CaptioningModels\
```

Example structure:
```
CaptioningModels/
??? Qwen3-VL-8B-Instruct-Q4_K_M.gguf              (~5.5 GB)
??? Qwen3-VL-8B-Instruct-mmproj-f16.gguf          (~1.6 GB)
??? Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf            (~5 GB)
??? Qwen2.5-VL-7B-Instruct-mmproj-f16.gguf        (~1.5 GB)
??? llava-v1.6-34b.Q4_K_M.gguf                    (~20 GB)
??? mmproj-model-f16.gguf                         (~600 MB)
```

## Usage

### Service Registration

```csharp
// In your DI configuration
services.AddCaptioningServices();

// Or with custom model path
services.AddCaptioningServices(@"D:\Models\Captioning");
```

### Basic Usage

```csharp
// Inject the service
private readonly ICaptioningService _captioningService;

// Check GPU availability
if (!_captioningService.IsGpuAvailable)
{
    Console.WriteLine("Warning: GPU not available, captioning will be slow!");
}

// Download a model (one-time) - Qwen 3 VL recommended
await _captioningService.DownloadModelAsync(
    CaptioningModelType.Qwen3_VL_8B,
    new Progress<ModelDownloadProgress>(p => 
        Console.WriteLine($"Download: {p.Percentage:F1}% - {p.Status}")));

// Load model into GPU memory
await _captioningService.LoadModelAsync(CaptioningModelType.Qwen3_VL_8B);

// Generate a single caption
var result = await _captioningService.GenerateSingleCaptionAsync(
    imagePath: @"C:\images\photo.jpg",
    systemPrompt: "Describe this image in detail",
    triggerWord: "photo_style",
    blacklistedWords: ["blurry", "low quality"]);

if (result.Success)
{
    Console.WriteLine($"Caption: {result.Caption}");
}
```

### Batch Processing

```csharp
// Configure batch job
var config = new CaptioningJobConfig(
    ImagePaths: Directory.GetFiles(@"C:\dataset", "*.jpg"),
    SelectedModel: CaptioningModelType.Qwen3_VL_8B,
    SystemPrompt: "Describe this image using natural language, focusing on the subject, style, and composition.",
    TriggerWord: "my_lora",
    BlacklistedWords: ["watermark", "signature", "text"],
    DatasetPath: @"C:\dataset",  // Where to save .txt files
    OverrideExisting: false,      // Skip already captioned images
    Temperature: 0.7f
);

// Validate configuration
var errors = config.Validate();
if (errors.Count > 0)
{
    throw new ArgumentException(string.Join("; ", errors));
}

// Process with progress
var results = await _captioningService.GenerateCaptionsAsync(
    config,
    new Progress<CaptioningProgress>(p =>
    {
        Console.WriteLine($"[{p.CurrentIndex}/{p.TotalCount}] {p.Status}");
        if (p.LastResult?.Success == true)
        {
            Console.WriteLine($"  Caption: {p.LastResult.Caption?[..50]}...");
        }
    }));

// Summarize results
var successful = results.Count(r => r.Success && !r.WasSkipped);
var skipped = results.Count(r => r.WasSkipped);
var failed = results.Count(r => !r.Success);

Console.WriteLine($"Completed: {successful} | Skipped: {skipped} | Failed: {failed}");
```

### Model Management

```csharp
// Get all model info
foreach (var model in _captioningService.GetAllModels())
{
    Console.WriteLine($"{model.DisplayName}");
    Console.WriteLine($"  Status: {model.Status}");
    Console.WriteLine($"  Size: {model.ExpectedSizeBytes / 1024 / 1024} MB");
    Console.WriteLine($"  Path: {model.FilePath}");
}

// Check specific model status
var info = _captioningService.GetModelInfo(CaptioningModelType.Qwen3_VL_8B);
if (info.Status == CaptioningModelStatus.Ready)
{
    // Model is downloaded and ready
}

// Unload model from GPU memory
_captioningService.UnloadModel();

// Delete model files
_captioningService.DeleteModel(CaptioningModelType.Qwen3_VL_8B);
```

## Configuration Options

### CaptioningJobConfig

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ImagePaths` | `IEnumerable<string>` | Required | List of image files to process |
| `SelectedModel` | `CaptioningModelType` | Required | Which VLM to use |
| `SystemPrompt` | `string` | "Describe the image using 100 English words" | Instructions for the AI |
| `TriggerWord` | `string?` | `null` | Token prepended to each caption |
| `BlacklistedWords` | `IReadOnlyList<string>?` | `null` | Words to remove from captions |
| `DatasetPath` | `string?` | Same as image | Output directory for .txt files |
| `OverrideExisting` | `bool` | `false` | Whether to overwrite existing captions |
| `Temperature` | `float` | `0.7` | Creativity (0.0=deterministic, 2.0=random) |

### Recommended System Prompts

For **LoRA Training**:
```
Describe this image in detail for training a LoRA model. Focus on the subject's appearance, 
clothing, pose, expression, and the background. Use natural, descriptive language.
```

For **General Captioning**:
```
Describe what you see in this image using natural English. Include details about the subject, 
colors, lighting, composition, and style.
```

For **Style Transfer**:
```
Describe the artistic style, color palette, and visual aesthetics of this image. 
Focus on the technique, mood, and artistic elements.
```

## Image Preprocessing

Before inference, images are automatically:

1. **Validated**: File existence, format, minimum size (100 bytes)
2. **Decoded**: Using SkiaSharp for broad format support
3. **Resized**: If larger than 2048px on any dimension (aspect ratio preserved)
4. **Encoded**: JPEG (90 quality) or PNG based on original format

### Supported Formats

- JPEG / JPG
- PNG
- WebP
- BMP
- GIF (first frame)

## Output Format

Caption files are saved as plain text files with the same base name as the image:

```
dataset/
??? image001.jpg
??? image001.txt    ? Generated caption
??? image002.png
??? image002.txt    ? Generated caption
```

Example caption content:
```
my_trigger, A portrait of a woman with long brown hair, wearing a blue dress. 
She is standing in a garden with roses in the background. The lighting is soft 
and natural, creating a warm atmosphere. The composition focuses on the upper body.
```

## Error Handling

### Common Errors

| Error | Cause | Solution |
|-------|-------|----------|
| "GPU not available" | CUDA not detected | Install NVIDIA drivers, verify CUDA support |
| "Model not downloaded" | Missing GGUF files | Call `DownloadModelAsync` first |
| "Failed to decode image" | Corrupted image file | Verify image opens in viewer |
| "Image too small" | Dimensions < 16px | Use higher resolution images |
| "Out of memory" | Insufficient VRAM | Use smaller model or close other GPU apps |

### Graceful Degradation

The service handles errors gracefully:
- Corrupt images are skipped with detailed error messages
- 0-byte files are detected and skipped
- Download interruptions leave partial files that are cleaned up

## Performance Tips

1. **Batch Size**: Process images in batches to avoid repeated model loading
2. **Image Size**: Smaller images (< 2048px) process faster
3. **GPU Memory**: Close other GPU-intensive apps before captioning
4. **Model Choice**: 
   - Use **Qwen 3 VL 8B** for best quality/speed ratio (recommended)
   - Use **Qwen 2.5 VL 7B** for proven stability
   - Use **LLaVA 34B** only if you have 24GB+ VRAM and need maximum quality
5. **Temperature**: Lower values (0.3-0.5) are faster than higher values

## Troubleshooting

### CUDA Not Detected

```powershell
# Check NVIDIA driver
nvidia-smi

# Verify CUDA version
nvcc --version
```

### Model Download Fails

1. Check internet connection
2. Verify disk space (25+ GB recommended)
3. Try downloading again (partial downloads are cleaned up)

### Out of Memory

1. Unload current model: `_captioningService.UnloadModel()`
2. Close GPU-intensive applications
3. Use a smaller model (Qwen 2.5 VL 7B instead of LLaVA 34B)

## API Reference

### ICaptioningService

```csharp
public interface ICaptioningService : IDisposable
{
    // Properties
    bool IsProcessing { get; }
    bool IsModelLoaded { get; }
    CaptioningModelType? LoadedModelType { get; }
    bool IsGpuAvailable { get; }

    // Model Management
    CaptioningModelInfo GetModelInfo(CaptioningModelType modelType);
    IReadOnlyList<CaptioningModelInfo> GetAllModels();
    Task<bool> DownloadModelAsync(CaptioningModelType modelType, ...);
    Task<bool> LoadModelAsync(CaptioningModelType modelType, ...);
    void UnloadModel();
    void DeleteModel(CaptioningModelType modelType);

    // Caption Generation
    Task<IReadOnlyList<CaptioningResult>> GenerateCaptionsAsync(CaptioningJobConfig config, ...);
    Task<CaptioningResult> GenerateSingleCaptionAsync(string imagePath, ...);
}
```

### Result Types

```csharp
// Caption result for a single image
public record CaptioningResult(
    bool Success,
    string ImagePath,
    string? Caption,
    string? CaptionFilePath,
    string? ErrorMessage,
    bool WasSkipped,
    string? SkipReason);

// Progress during batch processing
public record CaptioningProgress(
    int CurrentIndex,
    int TotalCount,
    string CurrentImagePath,
    string Status,
    CaptioningResult? LastResult);

// Model information
public record CaptioningModelInfo(
    CaptioningModelType ModelType,
    CaptioningModelStatus Status,
    string FilePath,
    long FileSizeBytes,
    long ExpectedSizeBytes,
    string DisplayName,
    string Description);
```

## Known Limitations

1. **NVIDIA Only**: CPU fallback is intentionally disabled for performance
2. **Windows Only**: CUDA 12 backend is Windows-specific
3. **Qwen 3 VL**: As a newer model, verify LlamaSharp version supports it
4. **Memory Usage**: Large models require significant system RAM during loading

## Future Enhancements

- [ ] Support for additional VLM architectures
- [ ] Automatic prompt optimization
- [ ] Caption quality scoring
- [ ] Integration with dataset management UI
- [ ] Batch queue with pause/resume
