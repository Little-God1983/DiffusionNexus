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
        entity.Property(e => e.MediaType).HasMaxLength(20);
        // Civitai's bitmask as a plain integer, in a column of its own (#631). The NSFW verdict is
        // derived from it (ModelVersion.IsImageNsfw), never stored.
        entity.Property(e => e.NsfwLevel).HasColumnName("NsfwRating").HasConversion<int>();

        // Dead columns kept for downgrade safety — see ModelImage.LegacyIsNsfw. Configured by name
        // so nothing compiles against the [Obsolete(error: true)] properties, and shaped exactly as
        // the snapshot had them (IsNsfw INTEGER NOT NULL, NsfwLevel TEXT NOT NULL max 10), so the
        // migration leaves both columns alone.
        entity.Property<bool>("LegacyIsNsfw").HasColumnName("IsNsfw");
        entity.Property<string>("LegacyNsfwLevel").HasColumnName("NsfwLevel").IsRequired().HasMaxLength(10);
        entity.Property(e => e.BlurHash).HasMaxLength(100);
        entity.Property(e => e.Username).HasMaxLength(200);
        entity.Property(e => e.LocalCachePath).HasMaxLength(500);

        // Thumbnail BLOB
        entity.Property(e => e.ThumbnailData).HasColumnType("BLOB");
        entity.Property(e => e.ThumbnailMimeType).HasMaxLength(50);
        entity.Property(e => e.ThumbnailFailure).HasMaxLength(30);

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
        entity.Ignore(e => e.IsVideo);
        entity.Ignore(e => e.HasLocalCache);
        entity.Ignore(e => e.IsPrimary);
        entity.Ignore(e => e.ThumbnailSizeKB);
    }
}
