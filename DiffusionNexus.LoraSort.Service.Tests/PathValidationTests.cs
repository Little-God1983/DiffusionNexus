using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace DiffusionNexus.LoraSort.Service.Tests;

public class PathValidationTests
{
    private class DummySettingsService : ISettingsService
    {
        public Task<SettingsModel> LoadAsync() => Task.FromResult(new SettingsModel());
        public Task SaveAsync(SettingsModel settings) => Task.CompletedTask;
    }

    [Fact]
    public void ValidatePaths_FailsForEmpty()
    {
        var vm = new LoraSortMainSettingsViewModel(new DummySettingsService());
        Assert.False(vm.ValidatePaths());
    }

    [Fact]
    public void IsPathTheSame_ReturnsTrue_ForIdentical()
    {
        var vm = new LoraSortMainSettingsViewModel(new DummySettingsService());
        var path = Path.Combine(Path.GetTempPath(), "samepath");
        vm.BasePath = path;
        vm.TargetPath = path;
        Assert.True(vm.IsPathTheSame());
    }

    [Fact]
    public void DiskSpaceCheck_ReturnsTrue_ForSmallFolder()
    {
        var svc = new DiffusionNexus.LoraSort.Service.Services.FileControllerService();
        var temp = Path.GetTempPath();
        var dir = Directory.CreateDirectory(Path.Combine(temp, "diskcheck"));
        try
        {
            Assert.True(svc.EnoughFreeSpaceOnDisk(dir.FullName, temp));
        }
        finally
        {
            Directory.Delete(dir.FullName, true);
        }
    }
}
