using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class BaseModelFolderConfiguration : IEntityTypeConfiguration<BaseModelFolder>
{
    public void Configure(EntityTypeBuilder<BaseModelFolder> entity)
    {
        entity.ToTable("BaseModelFolders");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.AppSettingsId);
        entity.HasIndex(e => e.FolderPath);

        // Properties
        entity.Property(e => e.FolderPath).IsRequired().HasMaxLength(1000);

        // The folder row survives removal of the installation it was registered for.
        entity.HasOne(e => e.InstallerPackage)
            .WithMany()
            .HasForeignKey(e => e.InstallerPackageId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
