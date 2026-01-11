# DiffusionNexus.Captioning

Local AI image captioning service using vision-language models with NVIDIA GPU acceleration.

## Overview

This project provides automatic image caption generation using local VLM (Vision-Language Model) inference. It leverages LlamaSharp with CUDA 12 backend for high-performance GPU-accelerated inference.

## Features

- ??? **Vision-Language Models**: LLaVA v1.6 34B and Qwen 2.5 VL 7B support
- ?? **GPU Accelerated**: NVIDIA CUDA 12 backend (supports RTX 50XX with CUDA 12.8)
- ?? **Model Management**: Automatic download, status tracking, file validation
- ?? **Batch Processing**: Process entire datasets with progress reporting
- ? **Image Preprocessing**: Automatic validation and resizing (max 2048px)
- ?? **Configurable**: Trigger words, blacklisted words, temperature control

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| LLamaSharp | 0.21.0 | LLM inference engine |
| LLamaSharp.Backend.Cuda12 | 0.21.0 | NVIDIA GPU acceleration |
| SkiaSharp | 3.119.1 | Image processing |
| Serilog | 4.3.0 | Logging |

## Project Structure

```
DiffusionNexus.Captioning/
??? CaptioningService.cs          # Main service implementation
??? CaptioningModelManager.cs     # Model download & file management
??? ImagePreprocessor.cs          # Image validation & resizing
??? ServiceCollectionExtensions.cs # DI registration
??? DiffusionNexus.Captioning.csproj
```

## Quick Start

```csharp
// Register services
services.AddCaptioningServices();

// Use the service
var service = provider.GetRequiredService<ICaptioningService>();

// Download and load a model
await service.DownloadModelAsync(CaptioningModelType.Qwen3_VL_8B);
await service.LoadModelAsync(CaptioningModelType.Qwen3_VL_8B);

// Generate captions
var config = new CaptioningJobConfig(
    ImagePaths: imageFiles,
    SelectedModel: CaptioningModelType.Qwen3_VL_8B,
    SystemPrompt: "Describe this image in detail",
    TriggerWord: "my_style"
);

var results = await service.GenerateCaptionsAsync(config);
```

## Requirements

- **GPU**: NVIDIA with CUDA 12.0+ support
- **VRAM**: 8+ GB (16+ GB for LLaVA 34B)
- **Disk**: 5-25 GB for model files
- **OS**: Windows 10/11 x64

## Documentation

See [docs/V2/Captioning.md](../docs/V2/Captioning.md) for comprehensive documentation including:

- Detailed API reference
- Configuration options
- Performance tips
- Troubleshooting guide

## Domain Types

Types defined in `DiffusionNexus.Domain`:

- `CaptioningModelType` - Enum for supported models
- `CaptioningJobConfig` - Job configuration record
- `ICaptioningService` - Service interface
- `CaptioningResult` - Single image result
- `CaptioningProgress` - Batch progress info
- `CaptioningModelStatus` - Model state enum
- `CaptioningModelInfo` - Model metadata record

## Model Storage

Models are stored in: `%LocalAppData%\DiffusionNexus\CaptioningModels\`

## License

Part of the DiffusionNexus project.
