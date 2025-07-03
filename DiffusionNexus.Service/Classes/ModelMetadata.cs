namespace DiffusionNexus.Service.Classes;

public class ModelMetadata
{
    public string? ModelId { get; set; }
    public string? ModelVersionName { get; set; }
    public string? BaseModel { get; set; }
    public DiffusionTypes ModelType { get; set; } = DiffusionTypes.OTHER;
    public List<string> Tags { get; set; } = new();
    public CivitaiBaseCategories Category { get; set; } = CivitaiBaseCategories.UNASSIGNED;
    public string? SHA256Hash { get; set; }
    public Dictionary<string, object> AdditionalData { get; set; } = new();
}
