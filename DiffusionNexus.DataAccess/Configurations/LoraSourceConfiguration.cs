using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class LoraSourceConfiguration : IEntityTypeConfiguration<LoraSource>
{
    public void Configure(EntityTypeBuilder<LoraSource> entity)
    {
        entity.ToTable("LoraSources");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.AppSettingsId);
        entity.HasIndex(e => e.FolderPath);

        // Properties
        entity.Property(e => e.FolderPath).IsRequired().HasMaxLength(1000);
    }
}
