namespace DiffusionNexus.Legacy.DataAccess.Entities;

public class ModelFile
{
    public int Id { get; set; }
    public int ModelVersionId { get; set; }
    public string CivitaiFileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double SizeKB { get; set; }
    public string? SHA256Hash { get; set; }
    public string? DownloadUrl { get; set; }
    public bool IsPrimary { get; set; }
    
    public string? LocalFilePath { get; set; }
    
    public string? PickleScanResult { get; set; }
    public string? VirusScanResult { get; set; }
    public DateTime? ScannedAt { get; set; }
    
    public ModelVersion ModelVersion { get; set; } = null!;
}
