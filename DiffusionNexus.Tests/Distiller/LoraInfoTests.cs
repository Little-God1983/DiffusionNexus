using DiffusionNexus.UI.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class LoraInfoTests
{
    [Fact]
    public void Source_defaults_to_null_and_is_settable()
    {
        var a = new LoraInfo { Name = "x" };
        a.Source.Should().BeNull();

        var b = new LoraInfo { Name = "x", Source = "Power Lora" };
        b.Source.Should().Be("Power Lora");
    }
}
