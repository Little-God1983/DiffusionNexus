using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ModelVersionConfiguration : IEntityTypeConfiguration<ModelVersion>
{
    public void Configure(EntityTypeBuilder<ModelVersion> entity)
    {
        entity.ToTable("ModelVersions");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.CivitaiId).IsUnique().HasFilter("[CivitaiId] IS NOT NULL");
        entity.HasIndex(e => e.ModelId);
        entity.HasIndex(e => e.BaseModel);
        entity.HasIndex(e => e.CreatedAt);

        // Properties
        entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
        entity.Property(e => e.Description).HasColumnType("TEXT");
        entity.Property(e => e.BaseModel).HasConversion<string>().HasMaxLength(50);
        entity.Property(e => e.BaseModelRaw).HasMaxLength(100);
        entity.Property(e => e.DownloadUrl).HasMaxLength(2000);

        // Relationships
        entity.HasMany(e => e.Files)
            .WithOne(f => f.ModelVersion)
            .HasForeignKey(f => f.ModelVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Images)
            .WithOne(i => i.ModelVersion)
            .HasForeignKey(i => i.ModelVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.TriggerWords)
            .WithOne(t => t.ModelVersion)
            .HasForeignKey(t => t.ModelVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ignore computed properties
        entity.Ignore(e => e.PrimaryFile);
        entity.Ignore(e => e.PrimaryImage);
        entity.Ignore(e => e.TriggerWordsText);
    }
}
