using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Exceptions;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.DataAccess.UnitOfWork;

public class UnitOfWorkTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public UnitOfWorkTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDataAccessLayer(options =>
            options.UseSqlite(_connection));

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext>();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task WhenSaveChangesThenPersistsAcrossRepositories()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await uow.Models.AddAsync(new Model { Name = "Model1" });
        await uow.AppSettings.AddAsync(new AppSettings { Id = 1 });
        var count = await uow.SaveChangesAsync();

        count.Should().BeGreaterThan(0);

        var models = await uow.Models.GetAllAsync();
        models.Should().HaveCount(1);
    }

    [Fact]
    public async Task WhenTransactionCommittedThenChangesArePersisted()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await uow.BeginTransactionAsync();

        await uow.Models.AddAsync(new Model { Name = "TransactionTest" });
        await uow.SaveChangesAsync();
        await uow.CommitTransactionAsync();

        var result = await uow.Models.FindAsync(m => m.Name == "TransactionTest");
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task WhenTransactionRolledBackThenChangesAreReverted()
    {
        // Insert a model first, then in a new scope try rollback
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var uow1 = scope1.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow1.Models.AddAsync(new Model { Name = "Existing" });
            await uow1.SaveChangesAsync();
        }

        using var scope2 = _serviceProvider.CreateScope();
        var uow2 = scope2.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await uow2.BeginTransactionAsync();
        await uow2.Models.AddAsync(new Model { Name = "ShouldRollback" });
        await uow2.SaveChangesAsync();
        await uow2.RollbackTransactionAsync();

        // After rollback, only the "Existing" model should remain
        var models = await uow2.Models.GetAllAsync();
        models.Should().HaveCount(1);
        models[0].Name.Should().Be("Existing");
    }

    [Fact]
    public async Task WhenDisclaimerAcceptedThenHasUserAcceptedReturnsTrue()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var acceptance = new DisclaimerAcceptance
        {
            WindowsUsername = "testuser",
            AcceptedAt = DateTimeOffset.UtcNow,
            Accepted = true
        };

        await uow.DisclaimerAcceptances.AddAsync(acceptance);
        await uow.SaveChangesAsync();

        var result = await uow.DisclaimerAcceptances.HasUserAcceptedAsync("testuser");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task WhenModelFileSearchedBySizeThenFindsCorrectFiles()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model { Name = "SizeTest" };
        var version = new ModelVersion { Name = "v1", Model = model };
        var file = new ModelFile
        {
            FileName = "test.safetensors",
            FileSizeBytes = 123456,
            LocalPath = "/old/path.safetensors",
            IsLocalFileValid = false,
            ModelVersion = version
        };
        version.Files.Add(file);
        model.Versions.Add(version);

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var candidates = await uow.ModelFiles.FindBySizeWithInvalidPathAsync(123456);
        candidates.Should().HaveCount(1);
        candidates[0].FileName.Should().Be("test.safetensors");
    }

    [Fact]
    public async Task WhenGetExistingLocalPathsThenReturnsHashSet()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model { Name = "PathTest" };
        var version = new ModelVersion { Name = "v1", Model = model };
        version.Files.Add(new ModelFile
        {
            FileName = "a.safetensors",
            LocalPath = "/path/a.safetensors",
            ModelVersion = version
        });
        version.Files.Add(new ModelFile
        {
            FileName = "b.safetensors",
            LocalPath = null,
            ModelVersion = version
        });
        model.Versions.Add(version);

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var paths = await uow.ModelFiles.GetExistingLocalPathsAsync();
        paths.Should().HaveCount(1);
        paths.Should().Contain("/path/a.safetensors");
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
