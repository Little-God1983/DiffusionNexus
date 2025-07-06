# DiffusionNexus

## Thumbnail Generation

DiffusionNexus automatically creates preview thumbnails for LoRA cards. When no static preview image is found but a matching `.gif` or `.mp4` exists, the application generates a WebP thumbnail on first load.

### Dependencies
- **Xabe.FFmpeg** – used to capture snapshots from video files. FFmpeg binaries are downloaded at runtime using `Xabe.FFmpeg.Downloader`.
- **SkiaSharp** – used for decoding GIF frames and encoding WebP images (already referenced transitively via Avalonia).

### Behaviour
1. On startup the application calls `FFmpeg.GetLatestVersion()` to download platform specific FFmpeg executables if they are not present.
2. When a card lacks a preview image, the first frame from a GIF or a snapshot from a video is converted to WebP and stored next to the media file. Subsequent runs reuse this cached file unless thumbnail settings change.
3. If thumbnail generation fails, no preview image is shown and the error is logged.

