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

        public static readonly string[] GeneralExtensions = [
        ".thumb.jpg",
        ".preview.png",
        ".preview.webp",
        ".metadata.json",
        ".webp",
        ".mp4",
        ".png",
        ".preview.jpeg",
        ".preview.jpg",
        ".cm-info.json",
        ".civitai.info",
        ".civitai",
        ".safetensors",
        ".thumb",
        ".json",
        ".pt"];
    }
}
