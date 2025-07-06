using System;

namespace DiffusionNexus.UI.Classes
{
    public static class ThumbnailSettings
    {
        public static int MaxWidth { get; set; } = 320;
        public static int JpegQuality { get; set; } = 80;
        public static TimeSpan VideoProbePosition { get; set; } = TimeSpan.FromSeconds(0.5);
    }
}
