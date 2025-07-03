using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.LoraSort.ViewModels;
public class PathValidationTests
{
    private static LoraSortMainSettingsViewModel CreateViewModel()
    {
        var mock = new Mock<ISettingsService>();
        mock.Setup(s => s.LoadAsync()).ReturnsAsync(new SettingsModel());
        mock.Setup(s => s.SaveAsync(It.IsAny<SettingsModel>())).Returns(Task.CompletedTask);
        return new LoraSortMainSettingsViewModel(mock.Object);
    }

    [Fact]
    public void ValidatePaths_FailsForEmpty()
    {
        var vm = CreateViewModel();
        vm.ValidatePaths().Should().BeFalse();
    }

    [Fact]
    public void IsPathTheSame_ReturnsTrue_ForIdentical()
    {
        var vm = CreateViewModel();
        var path = Path.Combine(Path.GetTempPath(), "samepath");
        vm.BasePath = path;
        vm.TargetPath = path;
        vm.IsPathTheSame().Should().BeTrue();
    }

    [Fact]
    public void DiskSpaceCheck_ReturnsTrue_ForSmallFolder()
    {
        var svc = new DiffusionNexus.Service.Services.FileControllerService();
        var temp = Path.GetTempPath();
        var dir = Directory.CreateDirectory(Path.Combine(temp, "diskcheck"));
        try
        {
            svc.EnoughFreeSpaceOnDisk(dir.FullName, temp).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir.FullName, true);
        }
    }
}
