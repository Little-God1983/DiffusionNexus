namespace DiffusionNexus.DataAccess.Entities;

public class ModelTag
{
    public int Id { get; set; }
    public int ModelId { get; set; }
    public string Tag { get; set; } = string.Empty;
    
    public Model Model { get; set; } = null!;
}
