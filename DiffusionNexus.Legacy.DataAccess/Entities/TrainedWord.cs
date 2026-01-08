namespace DiffusionNexus.Legacy.DataAccess.Entities;

public class TrainedWord
{
    public int Id { get; set; }
    public int ModelVersionId { get; set; }
    public string Word { get; set; } = string.Empty;
    
    public ModelVersion ModelVersion { get; set; } = null!;
}
