# DiffusionNexus

## Thumbnail Generation

Preview thumbnails are automatically created for GIF and video files using `SkiaSharp` and `Xabe.FFmpeg`. When no static preview image is found, the application generates a `.webp` thumbnail next to the media file on first load. FFmpeg binaries are downloaded at runtime via `Xabe.FFmpeg.Downloader` and cached per platform. Subsequent runs reuse existing thumbnails unless `ThumbnailSettings.MaxWidth` changes, in which case they are regenerated.
