namespace DiffusionNexus.DataAccess.Entities;

public class ModelVersion
{
    public int Id { get; set; }
    public int ModelId { get; set; }
    public string CivitaiVersionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string BaseModel { get; set; } = string.Empty;
    public string? BaseModelType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? Status { get; set; }
    public int NsfwLevel { get; set; }
    public string? DownloadUrl { get; set; }
    
    public Model Model { get; set; } = null!;
    public ICollection<ModelFile> Files { get; set; } = new List<ModelFile>();
    public ICollection<ModelImage> Images { get; set; } = new List<ModelImage>();
    public ICollection<TrainedWord> TrainedWords { get; set; } = new List<TrainedWord>();
}
