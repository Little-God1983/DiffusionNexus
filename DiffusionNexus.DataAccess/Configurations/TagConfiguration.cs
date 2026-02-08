using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> entity)
    {
        entity.ToTable("Tags");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.NormalizedName).IsUnique();

        // Properties
        entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
        entity.Property(e => e.NormalizedName).IsRequired().HasMaxLength(200);
    }
}
