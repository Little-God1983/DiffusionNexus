using System.Security.Cryptography;
using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Service;

/// <summary>
/// Covers the MOVE half of <see cref="ModelFileSyncService.DiscoverNewFilesAsync"/> (#537): a
/// scanned file whose size and hash match an existing invalid-path row is not a new model — the
/// row is re-pointed at the new path. That write is committed in the same SaveChanges as the new
/// models, and repoint candidates are by definition rows the grid hides, so the scan's result has
/// to say it happened or a repoint-only scan reads as "nothing changed".
/// </summary>
public sealed class ModelFileSyncServiceDiscoveryRepointTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _root;

    public ModelFileSyncServiceDiscoveryRepointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dn-repoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private IAppSettingsService SettingsWithRoot()
    {
        var mock = new Mock<IAppSettingsService>();
        mock.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([_root]);
        return mock.Object;
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// A model whose file row still points at a path that no longer exists, stamped invalid by a
    /// verify pass — the state a moved file leaves behind, and the state the grid hides.
    /// </summary>
    private async Task SeedMovedAwayModelAsync(string name, byte[] content)
    {
        var oldPath = Path.Combine(_root, "old", name);

        var model = new Model { Name = name, Type = ModelType.LORA, Source = DataSource.LocalFile };
        var version = new ModelVersion { Name = "v1", BaseModelRaw = "???", Model = model };
        version.Files.Add(new ModelFile
        {
            FileName = name,
            LocalPath = oldPath,
            HashSHA256 = Convert.ToHexString(SHA256.HashData(content)),
            FileSizeBytes = content.Length,
            IsPrimary = true,
            IsLocalFileValid = false,
            LocalFileVerifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
            ModelVersion = version,
        });
        model.Versions.Add(version);

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();
    }

    [Fact]
    public async Task ARepointOnlyScan_ReportsWhatItChangedNotJustWhatItAdded()
    {
        var weights = "moved model weights"u8.ToArray();
        await SeedMovedAwayModelAsync("moved.safetensors", weights);
        var newPath = WriteFile("moved.safetensors", weights);

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sut = new ModelFileSyncService(uow, SettingsWithRoot());

        var result = await sut.DiscoverNewFilesAsync();

        result.NewModels.Should().BeEmpty("the file is a move, not a new model");
        result.RepointedCount.Should().Be(1,
            "the row was durably re-pointed in this scan's own commit, and the caller's rebuild decision hangs on knowing it");

        using var verifyScope = _serviceProvider.CreateScope();
        var verifyUow = verifyScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var models = await verifyUow.Models.GetModelsWithLocalFilesLightAsync();
        var file = models.Single().Versions.Single().Files.Single();
        file.LocalPath.Should().Be(newPath, "the premise: the repoint itself really happened");
        file.IsLocalFileValid.Should().BeTrue();
    }

    [Fact]
    public async Task AScanWithGenuinelyNewFiles_DoesNotCountThemAsRepointed()
    {
        WriteFile("brand-new.safetensors", "new model weights"u8.ToArray());

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sut = new ModelFileSyncService(uow, SettingsWithRoot());

        var result = await sut.DiscoverNewFilesAsync();

        result.NewModels.Should().ContainSingle("an unknown file is a new model");
        result.RepointedCount.Should().Be(0, "nothing moved — the two counts answer different questions");
    }
}
