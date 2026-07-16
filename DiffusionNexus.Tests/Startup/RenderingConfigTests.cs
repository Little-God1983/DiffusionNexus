using DiffusionNexus.UI.Startup;

namespace DiffusionNexus.Tests.Startup;

public class RenderingConfigTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    public void UseSoftwareRendering_IsTrue_WhenEnvVarOptsIn(string value)
    {
        Assert.True(RenderingConfig.UseSoftwareRendering(_ => value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    public void UseSoftwareRendering_IsFalse_ByDefault(string? value)
    {
        Assert.False(RenderingConfig.UseSoftwareRendering(_ => value));
    }

    [Fact]
    public void UseSoftwareRendering_ReadsTheDocumentedVariable()
    {
        string? requested = null;
        RenderingConfig.UseSoftwareRendering(name => { requested = name; return null; });
        Assert.Equal("DIFFUSIONNEXUS_SOFTWARE_RENDERING", requested);
    }
}
