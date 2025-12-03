namespace DiffusionNexus.DataAccess.Entities;

public class ModelImage
{
    public int Id { get; set; }
    public int ModelVersionId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? LocalFilePath { get; set; }
    public int NsfwLevel { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Hash { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool Minor { get; set; }
    public bool Poi { get; set; }
    
    public ModelVersion ModelVersion { get; set; } = null!;
}
