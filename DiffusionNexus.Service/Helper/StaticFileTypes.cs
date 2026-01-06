/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */

namespace DiffusionNexus.Service.Helper
{
    internal static class StaticFileTypes
    {
        public static readonly string[] ModelExtensions =
        [
            ".ckpt",
            ".safetensors",
            ".pth",
            ".pt"
        ];

        /// <summary>
        /// Supported video file extensions for LoRA video training datasets.
        /// </summary>
        public static readonly string[] VideoExtensions =
        [
            ".mp4",
            ".mov",
            ".webm",
            ".avi",
            ".mkv",
            ".wmv",
            ".flv",
            ".m4v"
        ];

        /// <summary>
        /// Supported image file extensions for datasets.
        /// </summary>
        public static readonly string[] ImageExtensions =
        [
            ".png",
            ".jpg",
            ".jpeg",
            ".webp",
            ".bmp",
            ".gif"
        ];

        /// <summary>
        /// Combined media extensions (images + videos) for dataset handling.
        /// </summary>
        public static readonly string[] MediaExtensions =
        [
            // Images
            ".png",
            ".jpg",
            ".jpeg",
            ".webp",
            ".bmp",
            ".gif",
            // Videos
            ".mp4",
            ".mov",
            ".webm",
            ".avi",
            ".mkv",
            ".wmv",
            ".flv",
            ".m4v"
        ];

        public static readonly string[] GeneralExtensions = [
        ".thumb.jpg",
        ".preview.png",
        ".preview.webp",
        ".metadata.json",
        ".webp",
        ".mp4",
        ".mov",
        ".webm",
        ".avi",
        ".mkv",
        ".wmv",
        ".flv",
        ".m4v",
        ".png",
        ".preview.jpeg",
        ".preview.jpg",
        ".cm-info.json",
        ".civitai.info",
        ".civitai",
        ".safetensors",
        ".thumb",
        ".json",
        ".pt",
        ".yaml"];

        /// <summary>
        /// Checks if a file path is a video file.
        /// </summary>
        public static bool IsVideoFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            var extension = Path.GetExtension(filePath);
            return VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if a file path is an image file.
        /// </summary>
        public static bool IsImageFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            var extension = Path.GetExtension(filePath);
            return ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if a file path is a media file (image or video).
        /// </summary>
        public static bool IsMediaFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            var extension = Path.GetExtension(filePath);
            return MediaExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }
    }
}
