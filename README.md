# DiffusionNexus

DiffusionNexus is a cross‑platform desktop application for organising Stable Diffusion LoRA models and managing training datasets. It scans your collection, fetches metadata from Civitai and presents each model as a card with preview images. The project uses Avalonia so the same binaries run on Windows, Linux and macOS.

## Features

### LoRA Model Management
- **Thumbnail generation** – automatically creates WebP previews from GIF or video files when no static image is present.
- **Search & filtering** – instant search with autocomplete, folder tree filtering and sort options.
- **Metadata download** – retrieve missing information from the Civitai API using your API key.
- **Duplicate detection** – scan any folder for `.safetensors` files with identical content.
- **Clipboard helpers** – copy trained words or model names with a single click.

### Dataset Management
- **Version control** – organize training data into versioned datasets with branching support.
- **Image captioning** – edit and manage captions for training images.
- **Batch crop/scale** – resize images to standard aspect ratio buckets for LoRA training.
- **Image editor** – built-in editor with crop, rotate, color balance, brightness/contrast adjustments.
- **AI background removal** – remove backgrounds using RMBG-1.4 model (runs locally).
- **AI upscaling** – upscale images using 4x-UltraSharp model (runs locally).
- **Rating system** – mark images as production-ready or rejected for quality control.

## Installation

1. Install the [.NET 10 Runtime](https://dotnet.microsoft.com/download) if required.
2. Download a release archive from the [GitHub Releases](https://github.com/Little-God1983/DiffusionNexus/releases) page.
3. Extract the archive and run `DiffusionNexus.UI` (on Windows) or `dotnet DiffusionNexus.UI.dll` on Linux/macOS.

## Usage

A full walkthrough is available in the [User Guide](docs/user_guide.md). Configure paths and API keys under the **Settings** tab then open **Lora Helper** to browse your models.

## Development

### Prerequisites
- .NET 10 SDK
- Visual Studio 2022 or later / VS Code / Rider

### Building
```bash
dotnet build
```

### Running Tests
```bash
dotnet test
```

### Key Dependencies
- **Avalonia** – Cross-platform UI framework
- **Xabe.FFmpeg** – Downloads platform specific FFmpeg binaries on first run to capture frames from videos.
- **SkiaSharp** – Image processing, decodes GIFs and encodes WebP images.
- **Entity Framework Core** – SQLite database for metadata caching.
- **ONNX Runtime** – Local AI model inference for background removal and upscaling.

## Project Structure

| Project | Description |
|---------|-------------|
| `DiffusionNexus.UI` | Avalonia desktop application |
| `DiffusionNexus.Service` | Business logic and services |
| `DiffusionNexus.Domain` | Domain entities and interfaces |
| `DiffusionNexus.DataAccess` | Entity Framework Core database layer |
| `DiffusionNexus.Civitai` | Civitai API client |
| `DiffusionNexus.Tests` | Unit and integration tests |

## License

This repository is provided for non‑commercial use only. See the [LICENSE](LICENSE) file for details.

