using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class InstallerPackageConfiguration : IEntityTypeConfiguration<InstallerPackage>
{
    public void Configure(EntityTypeBuilder<InstallerPackage> entity)
    {
        entity.ToTable("InstallerPackages");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Name).IsRequired();
        entity.Property(e => e.InstallationPath).IsRequired();

        // 1:1 optional — FK on ImageGallery side
        // Every InstallerPackage has at most one ImageGallery.
        // Not every ImageGallery has an InstallerPackage (standalone galleries are allowed).
        entity.HasOne(e => e.ImageGallery)
            .WithOne(g => g.InstallerPackage)
            .HasForeignKey<ImageGallery>(g => g.InstallerPackageId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}