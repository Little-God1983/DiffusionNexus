using System;
using System.IO;

namespace DiffusionNexus.UI.Utilities;

public static class TemporaryDatasetConstants
{
    public const string GenerationGalleryTempDatasetName = "Temp Dataset";
    public const string GenerationGalleryTempDatasetPath = "temp://generation-gallery";
    public const string SourceMetadataExtension = ".source";

    public static readonly string GenerationGalleryTempRootPath =
        Path.Combine(Path.GetTempPath(), "DiffusionNexus", "GenerationGalleryTemp", Guid.NewGuid().ToString("N"));
}
