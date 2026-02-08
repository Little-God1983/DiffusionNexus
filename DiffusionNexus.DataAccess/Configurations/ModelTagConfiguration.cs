using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ModelTagConfiguration : IEntityTypeConfiguration<ModelTag>
{
    public void Configure(EntityTypeBuilder<ModelTag> entity)
    {
        entity.ToTable("ModelTags");

        // Composite primary key
        entity.HasKey(e => new { e.ModelId, e.TagId });

        // Relationships
        entity.HasOne(e => e.Model)
            .WithMany(m => m.Tags)
            .HasForeignKey(e => e.ModelId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Tag)
            .WithMany(t => t.Models)
            .HasForeignKey(e => e.TagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
