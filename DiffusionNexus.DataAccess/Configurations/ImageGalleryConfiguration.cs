using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ImageGalleryConfiguration : IEntityTypeConfiguration<ImageGallery>
{
    public void Configure(EntityTypeBuilder<ImageGallery> entity)
    {
        entity.ToTable("ImageGalleries");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.AppSettingsId);
        entity.HasIndex(e => e.FolderPath);

        // Properties
        entity.Property(e => e.FolderPath).IsRequired().HasMaxLength(1000);
    }
}
