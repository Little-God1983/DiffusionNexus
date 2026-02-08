using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ModelFileConfiguration : IEntityTypeConfiguration<ModelFile>
{
    public void Configure(EntityTypeBuilder<ModelFile> entity)
    {
        entity.ToTable("ModelFiles");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.CivitaiId);
        entity.HasIndex(e => e.ModelVersionId);
        entity.HasIndex(e => e.HashSHA256);
        entity.HasIndex(e => e.LocalPath);
        entity.HasIndex(e => e.FileSizeBytes);

        // Properties
        entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
        entity.Property(e => e.FileType).HasMaxLength(50);
        entity.Property(e => e.Format).HasConversion<string>().HasMaxLength(20);
        entity.Property(e => e.Precision).HasConversion<string>().HasMaxLength(10);
        entity.Property(e => e.SizeType).HasConversion<string>().HasMaxLength(10);
        entity.Property(e => e.PickleScanResult).HasConversion<string>().HasMaxLength(20);
        entity.Property(e => e.VirusScanResult).HasConversion<string>().HasMaxLength(20);
        entity.Property(e => e.DownloadUrl).HasMaxLength(2000);
        entity.Property(e => e.LocalPath).HasMaxLength(1000);
        entity.Property(e => e.HashAutoV1).HasMaxLength(20);
        entity.Property(e => e.HashAutoV2).HasMaxLength(20);
        entity.Property(e => e.HashSHA256).HasMaxLength(64);
        entity.Property(e => e.HashCRC32).HasMaxLength(10);
        entity.Property(e => e.HashBLAKE3).HasMaxLength(64);
        entity.Property(e => e.PickleScanMessage).HasMaxLength(1000);

        // Ignore computed properties
        entity.Ignore(e => e.SizeMB);
        entity.Ignore(e => e.SizeGB);
        entity.Ignore(e => e.SizeDisplay);
        entity.Ignore(e => e.IsSecure);
    }
}
