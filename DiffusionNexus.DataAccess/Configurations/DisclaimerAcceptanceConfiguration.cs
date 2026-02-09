using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class DisclaimerAcceptanceConfiguration : IEntityTypeConfiguration<DisclaimerAcceptance>
{
    public void Configure(EntityTypeBuilder<DisclaimerAcceptance> entity)
    {
        entity.ToTable("DisclaimerAcceptances");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.WindowsUsername);

        // Properties
        entity.Property(e => e.WindowsUsername).IsRequired().HasMaxLength(256);
    }
}
