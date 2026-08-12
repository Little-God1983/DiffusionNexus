using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ImageRatingOverrideConfiguration : IEntityTypeConfiguration<ImageRatingOverride>
{
    public void Configure(EntityTypeBuilder<ImageRatingOverride> entity)
    {
        entity.ToTable("ImageRatingOverrides");
        entity.HasKey(e => e.Id);

        entity.HasIndex(e => e.FilePath).IsUnique();

        // NOCASE for the same reason as ImageMediaTagIndexes.FilePath: Windows
        // paths are case-insensitive, and the override must match the index
        // row it overrides regardless of the casing either was stored with.
        // (Same Linux TODO applies as on ImageMediaTagIndexConfiguration.)
        entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1000).UseCollation("NOCASE");
    }
}
