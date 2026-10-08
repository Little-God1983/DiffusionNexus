using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class EngineRootResolverTests
{
    private static IServiceScopeFactory Scopes(IReadOnlyList<InstallerPackage> packages)
    {
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.InstallerPackages.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(packages);

        var services = new ServiceCollection();
        services.AddScoped(_ => uow.Object);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task NoAppManagedRow_ReturnsNull()
    {
        var resolver = new EngineRootResolver(Scopes(
        [
            new InstallerPackage { Id = 1, Name = "My ComfyUI", InstallationPath = @"D:\ComfyUI", ExecutablePath = "main.py", Type = InstallerType.ComfyUI }
        ]), syncModelPaths: (_, _) => Task.CompletedTask);

        (await resolver.ResolveAsync()).Should().BeNull("a user-managed ComfyUI is never the Engine");
    }

    [Fact]
    public async Task AppManagedRow_ReturnsItsPath_AndSyncsModelPathsFirst()
    {
        var synced = new List<string>();
        var resolver = new EngineRootResolver(Scopes(
        [
            new InstallerPackage { Id = 2, Name = "Diffusion Nexus Engine", InstallationPath = @"C:\Engine\ComfyUI", ExecutablePath = "main.py", Type = InstallerType.ComfyUI, IsAppManaged = true }
        ]), syncModelPaths: (root, _) => { synced.Add(root); return Task.CompletedTask; });

        (await resolver.ResolveAsync()).Should().Be(@"C:\Engine\ComfyUI");
        synced.Should().Equal(@"C:\Engine\ComfyUI");
    }
}
