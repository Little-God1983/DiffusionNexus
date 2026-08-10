using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ImageMediaTagIndexConfiguration : IEntityTypeConfiguration<ImageMediaTagIndex>
{
    public void Configure(EntityTypeBuilder<ImageMediaTagIndex> entity)
    {
        entity.ToTable("ImageMediaTagIndexes");
        entity.HasKey(e => e.Id);

        entity.HasIndex(e => e.FilePath).IsUnique();
        entity.HasIndex(e => e.RatingLabel);

        entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1000);
        entity.Property(e => e.RatingLabel).IsRequired().HasMaxLength(50);
    }
}
