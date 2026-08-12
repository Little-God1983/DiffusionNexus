using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ImageTagConfiguration : IEntityTypeConfiguration<ImageTag>
{
    public void Configure(EntityTypeBuilder<ImageTag> entity)
    {
        entity.ToTable("ImageTags");
        entity.HasKey(e => e.Id);
        entity.HasIndex(e => e.Name).IsUnique();
        // NOCASE: TagIndexService's tag lookup is OrdinalIgnoreCase, so
        // "Dog" and "dog" are one tag in memory. Without a matching collation
        // the unique index would let them become two rows, splitting a tag's
        // count across the tag cloud and breaking AND-search.
        entity.Property(e => e.Name).IsRequired().HasMaxLength(200).UseCollation("NOCASE");
    }
}
