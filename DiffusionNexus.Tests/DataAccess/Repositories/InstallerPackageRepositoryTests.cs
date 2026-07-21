using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.DataAccess.Repositories;

/// <summary>
/// Covers <c>InstallerPackageRepository.ClearDefaultByTypeAsync</c>, which enforces
/// the "at most one default per installer type" invariant used by the Installer Manager's
/// "Make default" command.
/// </summary>
public class InstallerPackageRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public InstallerPackageRepositoryTests()
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
    public async Task WhenNewDefaultIsSetThenThePreviousDefaultOfTheSameTypeIsCleared()
    {
        var comfyA = NewPackage("Comfy A", InstallerType.ComfyUI, isDefault: true);
        var comfyB = NewPackage("Comfy B", InstallerType.ComfyUI, isDefault: false);
        await SeedAsync(comfyA, comfyB);

        // Mirrors InstallerManagerViewModel.OnMakeDefaultRequestedAsync.
        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.InstallerPackages.ClearDefaultByTypeAsync(InstallerType.ComfyUI);

            var entity = await uow.InstallerPackages.GetByIdAsync(comfyB.Id);
            entity!.IsDefault = true;
            await uow.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var all = await uow.InstallerPackages.GetAllAsync();

            all.Single(p => p.Id == comfyA.Id).IsDefault.Should()
                .BeFalse("the previous ComfyUI default must be cleared");
            all.Single(p => p.Id == comfyB.Id).IsDefault.Should()
                .BeTrue("the newly selected package becomes the default");
            all.Count(p => p is { Type: InstallerType.ComfyUI, IsDefault: true }).Should()
                .Be(1, "only one default per installer type is allowed");
        }
    }

    [Fact]
    public async Task WhenDefaultIsClearedThenPackagesOfOtherTypesAreUntouched()
    {
        var comfy = NewPackage("Comfy", InstallerType.ComfyUI, isDefault: true);
        var forge = NewPackage("Forge", InstallerType.Forge, isDefault: true);
        var a1111 = NewPackage("A1111", InstallerType.Automatic1111, isDefault: true);
        await SeedAsync(comfy, forge, a1111);

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            // ExecuteUpdateAsync writes straight through — no SaveChangesAsync here on purpose.
            await uow.InstallerPackages.ClearDefaultByTypeAsync(InstallerType.ComfyUI);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var all = await uow.InstallerPackages.GetAllAsync();

            all.Single(p => p.Id == comfy.Id).IsDefault.Should()
                .BeFalse("the update is executed server-side without SaveChangesAsync");
            all.Single(p => p.Id == forge.Id).IsDefault.Should()
                .BeTrue("Forge is a different installer type");
            all.Single(p => p.Id == a1111.Id).IsDefault.Should()
                .BeTrue("A1111 is a different installer type");
        }
    }

    [Fact]
    public async Task WhenSeveralPackagesOfTheTypeAreDefaultThenAllOfThemAreCleared()
    {
        // Defensive: a corrupted DB may already violate the invariant.
        var comfyA = NewPackage("Comfy A", InstallerType.ComfyUI, isDefault: true);
        var comfyB = NewPackage("Comfy B", InstallerType.ComfyUI, isDefault: true);
        await SeedAsync(comfyA, comfyB);

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.InstallerPackages.ClearDefaultByTypeAsync(InstallerType.ComfyUI);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var all = await uow.InstallerPackages.GetAllAsync();

            all.Should().OnlyContain(p => !p.IsDefault);
        }
    }

    [Fact]
    public async Task WhenNoDefaultExistsThenClearingIsANoOp()
    {
        var comfyA = NewPackage("Comfy A", InstallerType.ComfyUI, isDefault: false);
        var comfyB = NewPackage("Comfy B", InstallerType.ComfyUI, isDefault: false);
        await SeedAsync(comfyA, comfyB);

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var act = () => uow.InstallerPackages.ClearDefaultByTypeAsync(InstallerType.ComfyUI);

            await act.Should().NotThrowAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var all = await uow.InstallerPackages.GetAllAsync();

            all.Should().HaveCount(2, "nothing may be deleted");
            all.Should().OnlyContain(p => !p.IsDefault);
        }
    }

    [Fact]
    public async Task WhenClearingATypeWithNoPackagesThenNothingChanges()
    {
        var comfy = NewPackage("Comfy", InstallerType.ComfyUI, isDefault: true);
        await SeedAsync(comfy);

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var act = () => uow.InstallerPackages.ClearDefaultByTypeAsync(InstallerType.SwarmUI);

            await act.Should().NotThrowAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var all = await uow.InstallerPackages.GetAllAsync();

            all.Should().ContainSingle().Which.IsDefault.Should().BeTrue();
        }
    }

    [Fact]
    public async Task WhenPackagesAreListedThenTheyAreOrderedByName()
    {
        await SeedAsync(
            NewPackage("Zeta", InstallerType.ComfyUI, isDefault: false),
            NewPackage("Alpha", InstallerType.Forge, isDefault: false),
            NewPackage("Mid", InstallerType.ComfyUI, isDefault: false));

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var all = await uow.InstallerPackages.GetAllAsync();

        all.Select(p => p.Name).Should().Equal(new[] { "Alpha", "Mid", "Zeta" });
    }

    [Fact]
    public async Task WhenPackageHasNoGalleryThenGetByIdWithGalleryReturnsItWithNullGallery()
    {
        var comfy = NewPackage("Comfy", InstallerType.ComfyUI, isDefault: false);
        await SeedAsync(comfy);

        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var loaded = await uow.InstallerPackages.GetByIdWithGalleryAsync(comfy.Id);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Comfy");
        loaded.ImageGallery.Should().BeNull();
    }

    [Fact]
    public async Task WhenPackageIdIsUnknownThenGetByIdWithGalleryReturnsNull()
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var loaded = await uow.InstallerPackages.GetByIdWithGalleryAsync(9999);

        loaded.Should().BeNull();
    }

    private async Task SeedAsync(params InstallerPackage[] packages)
    {
        using var scope = _serviceProvider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        foreach (var package in packages)
            await uow.InstallerPackages.AddAsync(package);

        await uow.SaveChangesAsync();
    }

    private static InstallerPackage NewPackage(string name, InstallerType type, bool isDefault)
        => new()
        {
            Name = name,
            InstallationPath = $@"C:\AI\{name.Replace(" ", string.Empty)}",
            ExecutablePath = null,
            Type = type,
            IsDefault = isDefault
        };

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
