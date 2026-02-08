using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class AppSettingsConfiguration : IEntityTypeConfiguration<AppSettings>
{
    public void Configure(EntityTypeBuilder<AppSettings> entity)
    {
        entity.ToTable("AppSettings");
        entity.HasKey(e => e.Id);

        // Properties
        entity.Property(e => e.EncryptedCivitaiApiKey).HasMaxLength(2000);
        entity.Property(e => e.LoraSortSourcePath).HasMaxLength(1000);
        entity.Property(e => e.LoraSortTargetPath).HasMaxLength(1000);
        entity.Property(e => e.DatasetStoragePath).HasMaxLength(1000);
        entity.Property(e => e.AutoBackupLocation).HasMaxLength(1000);
        entity.Property(e => e.ComfyUiServerUrl).HasMaxLength(2000).HasDefaultValue("http://127.0.0.1:8188/");

        // Relationships
        entity.HasMany(e => e.LoraSources)
            .WithOne(s => s.AppSettings)
            .HasForeignKey(s => s.AppSettingsId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.DatasetCategories)
            .WithOne(c => c.AppSettings)
            .HasForeignKey(c => c.AppSettingsId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.ImageGalleries)
            .WithOne(g => g.AppSettings)
            .HasForeignKey(g => g.AppSettingsId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
