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

    [Fact]
    public void ResolveMainPy_ReturnsNullForNullOrMissingFolder()
    {
        ManagedEngineLocator.ResolveMainPy(null).Should().BeNull();
        ManagedEngineLocator.ResolveMainPy("   ").Should().BeNull();
        ManagedEngineLocator.ResolveMainPy(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid()))
            .Should().BeNull();
    }

    [Fact]
    public void ResolveMainPy_SupportsBothLayoutsAndPrefersTheDirectOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            ManagedEngineLocator.ResolveMainPy(root).Should().BeNull("an empty folder is not an install");

            var nestedDir = Path.Combine(root, "ComfyUI");
            Directory.CreateDirectory(nestedDir);
            var nestedMainPy = Path.Combine(nestedDir, "main.py");
            File.WriteAllText(nestedMainPy, "# comfy");
            ManagedEngineLocator.ResolveMainPy(root).Should().Be(nestedMainPy,
                "the nested ComfyUI/ layout is also supported");

            var directMainPy = Path.Combine(root, "main.py");
            File.WriteAllText(directMainPy, "# comfy");
            ManagedEngineLocator.ResolveMainPy(root).Should().Be(directMainPy,
                "the direct layout takes priority when both exist");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
