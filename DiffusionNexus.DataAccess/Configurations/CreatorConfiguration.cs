using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class CreatorConfiguration : IEntityTypeConfiguration<Creator>
{
    public void Configure(EntityTypeBuilder<Creator> entity)
    {
        entity.ToTable("Creators");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.Username).IsUnique();

        // Properties
        entity.Property(e => e.Username).IsRequired().HasMaxLength(200);
        entity.Property(e => e.AvatarUrl).HasMaxLength(2000);

        // Ignore computed properties
        entity.Ignore(e => e.ModelCount);
    }
}
