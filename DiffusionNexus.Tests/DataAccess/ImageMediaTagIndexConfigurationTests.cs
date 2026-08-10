using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DiffusionNexus.Tests.DataAccess;

public sealed class ImageMediaTagIndexConfigurationTests : IDisposable
{
    private readonly string _dir;
    private readonly DiffusionNexusCoreDbContext _context;

    public ImageMediaTagIndexConfigurationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dn-tagcfg-" + Guid.NewGuid().ToString("N"));
        var options = DiffusionNexusCoreDbContext.CreateOptions(_dir);
        _context = new DiffusionNexusCoreDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task CanInsertImageWithTagsAndReadThemBack()
    {
        var index = new ImageMediaTagIndex
        {
            FilePath = @"C:\gallery\sample.png",
            FileSizeBytes = 12345,
            FileLastWriteTimeUtc = DateTime.UtcNow,
            RatingLabel = "general",
            RatingScore = 0.9f,
            IsNsfw = false,
            IndexedAtUtc = DateTimeOffset.UtcNow,
        };
        var dogTag = new ImageTag { Name = "dog" };
        index.TagAssignments.Add(new ImageMediaTagAssignment { ImageMediaTagIndex = index, ImageTag = dogTag, Confidence = 0.87f });

        _context.ImageMediaTagIndexes.Add(index);
        await _context.SaveChangesAsync();

        var reloaded = await _context.ImageMediaTagIndexes
            .Include(e => e.TagAssignments).ThenInclude(a => a.ImageTag)
            .SingleAsync(e => e.FilePath == @"C:\gallery\sample.png");

        reloaded.RatingLabel.Should().Be("general");
        reloaded.TagAssignments.Should().ContainSingle(a => a.ImageTag!.Name == "dog" && Math.Abs(a.Confidence - 0.87f) < 0.001f);
    }

    [Fact]
    public async Task FilePath_MustBeUnique()
    {
        _context.ImageMediaTagIndexes.Add(new ImageMediaTagIndex
        {
            FilePath = @"C:\gallery\dup.png", RatingLabel = "general", IndexedAtUtc = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        _context.ImageMediaTagIndexes.Add(new ImageMediaTagIndex
        {
            FilePath = @"C:\gallery\dup.png", RatingLabel = "general", IndexedAtUtc = DateTimeOffset.UtcNow,
        });

        var act = async () => await _context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ImageTag_Name_MustBeUnique()
    {
        _context.ImageTags.Add(new ImageTag { Name = "dog" });
        await _context.SaveChangesAsync();
        _context.ImageTags.Add(new ImageTag { Name = "dog" });

        var act = async () => await _context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
