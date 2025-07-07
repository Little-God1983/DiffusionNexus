using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DiffusionNexus.Service.Classes
{
    public static class SupportedTypes
    {
        public static string[] ImageTypesByPriority = [
          ".thumb.jpg",
            ".webp",
            "jpeg",
            "jpg",
            ".preview.webp",
            ".preview.jpeg",
            ".preview.jpg",
            ".preview.png",
        ];

        public static string[] VideoTypesByPriority = [
            ".mp4",
            ".gif",
            ".webm",
            ".mov",
            ".mkv"
        ];
        public static string[] ModelMetadataFilesByPriority = [
            "civitai.info",
            "civitai.json",
        ];

        public static string[] ModelTypesByPriority = [
            ".safetensors",
            ".ckpt",
            ".pt",
            ".bin",
            ".pth",
            ".onnx"
        ];
    }
}
