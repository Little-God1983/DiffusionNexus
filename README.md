# DiffusionNexus

DiffusionNexus is a cross‑platform desktop application for organising Stable Diffusion LoRA models. It scans your collection, fetches metadata from Civitai and presents each model as a card with preview images. The project uses Avalonia so the same binaries run on Windows, Linux and macOS.

## Features
- **Thumbnail generation** – automatically creates WebP previews from GIF or video files when no static image is present.
- **Search & filtering** – instant search with autocomplete, folder tree filtering and sort options.
- **Metadata download** – retrieve missing information from the Civitai API using your API key.
- **Duplicate detection** – scan any folder for `.safetensors` files with identical content.
- **Clipboard helpers** – copy trained words or model names with a single click.

## Installation
1. Install the [.NET 8 Runtime](https://dotnet.microsoft.com/download) if required.
2. Download a release archive from the [GitHub Releases](https://github.com/<REPO>/releases) page.
3. Extract the archive and run `DiffusionNexus.UI` (on Windows) or `dotnet DiffusionNexus.UI.dll` on Linux/macOS.

## Usage
A full walkthrough is available in the [User Guide](docs/user_guide.md). Configure paths and API keys under the **Settings** tab then open **Lora Helper** to browse your models.

## Development
Thumbnail creation relies on these libraries:
- **Xabe.FFmpeg** – downloads platform specific FFmpeg binaries on first run to capture frames from videos.
- **SkiaSharp** – decodes GIFs and encodes WebP images.

The application calls `FFmpeg.GetLatestVersion()` during startup to ensure the executables are present. Generated thumbnails are cached next to the media file.

## License
This repository is provided for non‑commercial use only. See the [LICENSE](LICENSE) file for details.

