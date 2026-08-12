using DiffusionNexus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiffusionNexus.DataAccess.Configurations;

internal sealed class ImageMediaTagIndexConfiguration : IEntityTypeConfiguration<ImageMediaTagIndex>
{
    public void Configure(EntityTypeBuilder<ImageMediaTagIndex> entity)
    {
        entity.ToTable("ImageMediaTagIndexes");
        entity.HasKey(e => e.Id);

        entity.HasIndex(e => e.FilePath).IsUnique();
        entity.HasIndex(e => e.RatingLabel);

        // NOCASE, because the unique index above is on a Windows file path and
        // Windows paths are case-insensitive: SQLite's default BINARY
        // collation would happily store "C:\Gallery\a.png" and
        // "c:\gallery\A.PNG" as two rows for one file. TagIndexService also
        // de-duplicates its input case-insensitively, so this keeps the DB and
        // the in-memory side of the indexer on the same definition of "same
        // file".
        // TODO: Linux Implementation — on case-sensitive filesystems
        // /gallery/a.png and /gallery/A.PNG are distinct files that this
        // NOCASE unique index would collapse into one row; a Linux port needs
        // a per-platform collation choice (and the matching comparer in
        // TagIndexService).
        entity.Property(e => e.FilePath).IsRequired().HasMaxLength(1000).UseCollation("NOCASE");
        entity.Property(e => e.RatingLabel).IsRequired().HasMaxLength(50);
    }
}
