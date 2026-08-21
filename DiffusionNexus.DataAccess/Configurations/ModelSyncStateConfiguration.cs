using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ModelSyncStateConfiguration : IEntityTypeConfiguration<ModelSyncState>
{
    public void Configure(EntityTypeBuilder<ModelSyncState> entity)
    {
        entity.ToTable("ModelSyncStates");
        // PK == FK: exactly one state row per model; deleting the model deletes its state.
        entity.HasKey(e => e.ModelId);

        entity.Property(e => e.MetadataOutcome).HasConversion<string>().HasMaxLength(20);
        entity.Property(e => e.LastError).HasMaxLength(500);
        entity.Property(e => e.SidecarSignature).HasMaxLength(1100);

        entity.HasOne(e => e.Model)
            .WithOne(m => m.SyncState)
            .HasForeignKey<ModelSyncState>(e => e.ModelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
