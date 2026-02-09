using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class TriggerWordConfiguration : IEntityTypeConfiguration<TriggerWord>
{
    public void Configure(EntityTypeBuilder<TriggerWord> entity)
    {
        entity.ToTable("TriggerWords");
        entity.HasKey(e => e.Id);

        // Indexes
        entity.HasIndex(e => e.ModelVersionId);

        // Properties
        entity.Property(e => e.Word).IsRequired().HasMaxLength(500);
    }
}
