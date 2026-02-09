using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ModelImageConfiguration : IEntityTypeConfiguration<ModelImage>
{
    public void Configure(EntityTypeBuilder<ModelImage> entity)
    {
        entity.ToTable("ModelImages");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.CivitaiId);
        entity.HasIndex(e => e.ModelVersionId);
        entity.HasIndex(e => new { e.ModelVersionId, e.SortOrder });

        // Properties
        entity.Property(e => e.Url).IsRequired().HasMaxLength(2000);
        entity.Property(e => e.NsfwLevel).HasConversion<string>().HasMaxLength(10);
        entity.Property(e => e.BlurHash).HasMaxLength(100);
        entity.Property(e => e.Username).HasMaxLength(200);
        entity.Property(e => e.LocalCachePath).HasMaxLength(500);

        // Thumbnail BLOB
        entity.Property(e => e.ThumbnailData).HasColumnType("BLOB");
        entity.Property(e => e.ThumbnailMimeType).HasMaxLength(50);

        // Generation metadata
        entity.Property(e => e.Prompt).HasColumnType("TEXT");
        entity.Property(e => e.NegativePrompt).HasColumnType("TEXT");
        entity.Property(e => e.Sampler).HasMaxLength(100);
        entity.Property(e => e.GenerationModel).HasMaxLength(200);

        // Ignore computed properties
        entity.Ignore(e => e.AspectRatio);
        entity.Ignore(e => e.IsPortrait);
        entity.Ignore(e => e.IsLandscape);
        entity.Ignore(e => e.HasThumbnail);
        entity.Ignore(e => e.HasLocalCache);
        entity.Ignore(e => e.IsPrimary);
        entity.Ignore(e => e.ThumbnailSizeKB);
    }
}
