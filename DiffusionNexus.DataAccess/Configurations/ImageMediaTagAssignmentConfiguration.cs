using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ImageMediaTagAssignmentConfiguration : IEntityTypeConfiguration<ImageMediaTagAssignment>
{
    public void Configure(EntityTypeBuilder<ImageMediaTagAssignment> entity)
    {
        entity.ToTable("ImageMediaTagAssignments");
        entity.HasKey(e => new { e.ImageMediaTagIndexId, e.ImageTagId });

        entity.HasOne(e => e.ImageMediaTagIndex)
            .WithMany(e => e.TagAssignments)
            .HasForeignKey(e => e.ImageMediaTagIndexId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.ImageTag)
            .WithMany(e => e.Assignments)
            .HasForeignKey(e => e.ImageTagId)
            .OnDelete(DeleteBehavior.Cascade);

        // Supports both "all tags for image X" (via the FK itself) and the
        // tag-cloud / "all images with tag Y" queries.
        entity.HasIndex(e => e.ImageTagId);
    }
}
