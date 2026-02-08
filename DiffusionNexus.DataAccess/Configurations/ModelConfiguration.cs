using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ModelConfiguration : IEntityTypeConfiguration<Model>
{
    public void Configure(EntityTypeBuilder<Model> entity)
    {
        entity.ToTable("Models");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.CivitaiId).IsUnique().HasFilter("[CivitaiId] IS NOT NULL");
        entity.HasIndex(e => e.Name);
        entity.HasIndex(e => e.Type);
        entity.HasIndex(e => e.CreatedAt);

        // Properties
        entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
        entity.Property(e => e.Description).HasColumnType("TEXT");
        entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(50);
        entity.Property(e => e.Mode).HasConversion<string>().HasMaxLength(20);
        entity.Property(e => e.Source).HasConversion<string>().HasMaxLength(20);
        entity.Property(e => e.AllowCommercialUse).HasConversion<string>().HasMaxLength(20);

        // Relationships
        entity.HasOne(e => e.Creator)
            .WithMany(c => c.Models)
            .HasForeignKey(e => e.CreatorId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasMany(e => e.Versions)
            .WithOne(v => v.Model)
            .HasForeignKey(v => v.ModelId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ignore computed properties
        entity.Ignore(e => e.LatestVersion);
        entity.Ignore(e => e.TotalDownloads);
        entity.Ignore(e => e.PrimaryImage);
    }
}
