namespace DiffusionNexus.DataAccess.Entities;

public class Model
{
    public int Id { get; set; }
    public string CivitaiModelId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool Nsfw { get; set; }
    public int NsfwLevel { get; set; }
    public bool AllowNoCredit { get; set; }
    public string? AllowCommercialUse { get; set; }
    public bool AllowDerivatives { get; set; }
    public int UserId { get; set; }
    public string? CreatorUsername { get; set; }
    public string? CreatorImage { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<ModelVersion> Versions { get; set; } = new List<ModelVersion>();
    public ICollection<ModelTag> Tags { get; set; } = new List<ModelTag>();
}
