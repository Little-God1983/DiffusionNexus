/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */

using DiffusionNexus.Domain.Enums;

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
        public static string[] VideoExtensions => SupportedMediaTypes.VideoExtensions;

        /// <summary>
        /// Supported image file extensions for datasets.
        /// </summary>
        public static string[] ImageExtensions => SupportedMediaTypes.ImageExtensions;

        /// <summary>
        /// Combined media extensions (images + videos) for dataset handling.
        /// </summary>
        public static string[] MediaExtensions => SupportedMediaTypes.MediaExtensions;

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
                public static bool IsVideoFile(string filePath) => SupportedMediaTypes.IsVideoFile(filePath);

                /// <summary>
                /// Checks if a file path is an image file.
                /// </summary>
                public static bool IsImageFile(string filePath) => SupportedMediaTypes.IsImageFile(filePath);

                /// <summary>
                /// Checks if a file path is a media file (image or video).
                /// </summary>
                public static bool IsMediaFile(string filePath) => SupportedMediaTypes.IsMediaFile(filePath);
            }
        }
