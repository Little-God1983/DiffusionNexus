namespace DiffusionNexus.Service.Classes;

public class ModelMetadata
{
    public string ModelId { get; set; } = string.Empty;
    public string ModelVersionName { get; set; } = string.Empty;
    public string BaseModel { get; set; } = string.Empty;
    public DiffusionTypes ModelType { get; set; } = DiffusionTypes.OTHER;
    public List<string> Tags { get; set; } = new();
    public CivitaiBaseCategories Category { get; set; } = CivitaiBaseCategories.UNASSIGNED;
    public string SHA256Hash { get; set; } = string.Empty;
    public Dictionary<string, object> AdditionalData { get; set; } = new();
}
