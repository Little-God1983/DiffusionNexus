using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.DataAccess.Repositories;

public class ModelRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public ModelRepositoryTests()
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
    public async Task WhenModelAddedThenGetByIdReturnsIt()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model
        {
            Name = "TestLora",
            Type = ModelType.LORA,
            Source = DataSource.LocalFile
        };

        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var result = await uow.Models.GetByIdAsync(model.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("TestLora");
    }

    [Fact]
    public async Task WhenGetModelsWithLocalFilesThenReturnsOnlyModelsWithLocalFiles()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var modelWithFile = CreateModelWithLocalFile("WithFile", "/path/to/file.safetensors");
        var modelWithoutFile = CreateModelWithLocalFile("WithoutFile", localPath: null);

        await uow.Models.AddAsync(modelWithFile);
        await uow.Models.AddAsync(modelWithoutFile);
        await uow.SaveChangesAsync();

        var results = await uow.Models.GetModelsWithLocalFilesAsync();

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("WithFile");
    }

    [Fact]
    public async Task WhenGetAllWithIncludesThenNavigationPropertiesAreLoaded()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = CreateModelWithLocalFile("FullModel", "/path/file.safetensors");
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        var results = await uow.Models.GetAllWithIncludesAsync();

        results.Should().HaveCount(1);
        results[0].Versions.Should().HaveCount(1);
        results[0].Versions.First().Files.Should().HaveCount(1);
    }

    [Fact]
    public async Task WhenFindByPredicateThenReturnsMatchingModels()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await uow.Models.AddAsync(new Model { Name = "LoraA", Type = ModelType.LORA });
        await uow.Models.AddAsync(new Model { Name = "Checkpoint", Type = ModelType.Checkpoint });
        await uow.SaveChangesAsync();

        var loras = await uow.Models.FindAsync(m => m.Type == ModelType.LORA);

        loras.Should().HaveCount(1);
        loras[0].Name.Should().Be("LoraA");
    }

    [Fact]
    public async Task WhenRemoveModelThenItIsDeleted()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = new Model { Name = "ToDelete" };
        await uow.Models.AddAsync(model);
        await uow.SaveChangesAsync();

        uow.Models.Remove(model);
        await uow.SaveChangesAsync();

        var result = await uow.Models.GetByIdAsync(model.Id);
        result.Should().BeNull();
    }

    [Fact]
    public async Task WhenDuplicateCivitaiIdAssignedToModelThenSaveThrows()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var modelA = new Model { Name = "ModelA", Type = ModelType.LORA, CivitaiId = 42 };
        var modelB = new Model { Name = "ModelB", Type = ModelType.LORA };
        await uow.Models.AddAsync(modelA);
        await uow.Models.AddAsync(modelB);
        await uow.SaveChangesAsync();

        modelB.CivitaiId = 42;

        var act = () => uow.SaveChangesAsync();
        await act.Should().ThrowAsync<DiffusionNexus.DataAccess.Exceptions.DatabaseOperationException>()
            .WithMessage("*UNIQUE constraint*");
    }

    [Fact]
    public async Task WhenDuplicateCivitaiIdAssignedToVersionThenSaveThrows()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var modelA = CreateModelWithLocalFile("ModelA", "/a.safetensors");
        modelA.Versions.First().CivitaiId = 99;
        var modelB = CreateModelWithLocalFile("ModelB", "/b.safetensors");
        await uow.Models.AddAsync(modelA);
        await uow.Models.AddAsync(modelB);
        await uow.SaveChangesAsync();

        modelB.Versions.First().CivitaiId = 99;

        var act = () => uow.SaveChangesAsync();
        await act.Should().ThrowAsync<DiffusionNexus.DataAccess.Exceptions.DatabaseOperationException>()
            .WithMessage("*UNIQUE constraint*");
    }

    [Fact]
    public async Task WhenCivitaiIdOwnershipCheckedThenDuplicateIsAvoided()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var modelA = new Model { Name = "ModelA", Type = ModelType.LORA, CivitaiId = 42 };
        var modelB = new Model { Name = "ModelB", Type = ModelType.LORA };
        await uow.Models.AddAsync(modelA);
        await uow.Models.AddAsync(modelB);
        await uow.SaveChangesAsync();

        // Guard: only assign if no other model owns the CivitaiId
        var allModels = await uow.Models.GetAllAsync();
        var existingOwner = allModels.FirstOrDefault(m => m.CivitaiId == 42);
        if (existingOwner is null || existingOwner.Id == modelB.Id)
        {
            modelB.CivitaiId = 42;
        }

        // Save should succeed because the guard prevented the duplicate assignment
        var act = () => uow.SaveChangesAsync();
        await act.Should().NotThrowAsync();

        // modelB should still have no CivitaiId
        modelB.CivitaiId.Should().BeNull();
    }

    private static Model CreateModelWithLocalFile(string name, string? localPath)
    {
        var model = new Model
        {
            Name = name,
            Type = ModelType.LORA,
            Source = DataSource.LocalFile
        };

        var version = new ModelVersion
        {
            Name = name,
            BaseModel = BaseModelType.Other,
            Model = model
        };

        var file = new ModelFile
        {
            FileName = $"{name}.safetensors",
            LocalPath = localPath,
            IsPrimary = true,
            IsLocalFileValid = localPath is not null,
            ModelVersion = version
        };

        version.Files.Add(file);
        model.Versions.Add(version);
        return model;
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
