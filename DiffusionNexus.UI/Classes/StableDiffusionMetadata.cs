using System;

namespace DiffusionNexus.UI.Classes
{
    public class StableDiffusionMetadata
    {
        public string? Prompt { get; set; }
        public string? NegativePrompt { get; set; }
        public int Steps { get; set; }
        public string? Sampler { get; set; }
        public float CFGScale { get; set; }
        public long Seed { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? ModelHash { get; set; }
    }
}

