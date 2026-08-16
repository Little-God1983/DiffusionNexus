using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class ManagedEngineLocatorTests
{
    [Fact]
    public void DefaultInstallRoot_LivesUnderLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        ManagedEngineLocator.DefaultInstallRoot.Should().StartWith(localAppData);
        ManagedEngineLocator.DefaultInstallRoot.Should().EndWith(Path.Combine("DiffusionNexus", "Engine", "ComfyUI"));
    }

    [Fact]
    public void LooksInstalled_IsFalseForNullOrMissingFolder()
    {
        ManagedEngineLocator.LooksInstalled(null).Should().BeFalse();
        ManagedEngineLocator.LooksInstalled("   ").Should().BeFalse();
        ManagedEngineLocator.LooksInstalled(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid()))
            .Should().BeFalse();
    }

    [Fact]
    public void LooksInstalled_IsTrueOnlyWhenComfyUiEntryPointExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            ManagedEngineLocator.LooksInstalled(root).Should().BeFalse("an empty folder is not an install");

            File.WriteAllText(Path.Combine(root, "main.py"), "# comfy");
            ManagedEngineLocator.LooksInstalled(root).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
