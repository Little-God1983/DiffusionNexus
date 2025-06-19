using System;

namespace DiffusionNexus.UI.Classes
{
    public class StableDiffusionMetadata
    {
        public string? Prompt { get; set; }
        public string? NegativePrompt { get; set; }
        public int Steps { get; set; }
        public string? Sampler { get; set; }
        public string? ScheduleType { get; set; }
        public float CFGScale { get; set; }
        public long Seed { get; set; }
        public string? FaceRestoration { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? ModelHash { get; set; }
        public string? Model { get; set; }
        public string? TI { get; set; }
        public string? Version { get; set; }
        public string? SourceIdentifier { get; set; }
        public string? LoRAHashes { get; set; }
        public string? Hashes { get; set; }
        public string? Resources { get; set; }
    }
}

