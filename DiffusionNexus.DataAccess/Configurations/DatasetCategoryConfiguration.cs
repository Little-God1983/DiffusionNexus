using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class DatasetCategoryConfiguration : IEntityTypeConfiguration<DatasetCategory>
{
    public void Configure(EntityTypeBuilder<DatasetCategory> entity)
    {
        entity.ToTable("DatasetCategories");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.AppSettingsId);
        entity.HasIndex(e => e.Name);

        // Properties
        entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
        entity.Property(e => e.Description).HasMaxLength(500);
    }
}
