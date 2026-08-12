namespace DiffusionNexus.Domain.Entities;

/// <summary>Join row: one image carries one tag, at the confidence the tagger reported.</summary>
public class ImageMediaTagAssignment
{
    public int ImageMediaTagIndexId { get; set; }
    public ImageMediaTagIndex? ImageMediaTagIndex { get; set; }

    public int ImageTagId { get; set; }
    public ImageTag? ImageTag { get; set; }

    public float Confidence { get; set; }
}
