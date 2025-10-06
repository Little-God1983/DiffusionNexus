using DiffusionNexus.UI.Classes;
using DiffusionNexus.Service.Classes;
using FluentAssertions;
using System.Collections.Generic;
using System.IO;

namespace DiffusionNexus.Tests.LoraSort.Classes;

public class LoraVariantClassifierTests
{
    [Theory]
    [InlineData("wriggling_t2v_high_e100.safetensors", "wrigglingt2ve100", "High")]
    [InlineData("wriggling_t2v_low_e100.safetensors", "wrigglingt2ve100", "Low")]
    [InlineData("WANTT2VHIGHNOISEJIGGLE", "wantt2vjiggle", "High")]
    [InlineData("WANTT2VLOWNOISEJIGGLE", "wantt2vjiggle", "Low")]
    [InlineData("Pump_wan22_e20_high.safetensors", "pumpwan22e20", "High")]
    [InlineData("Pump_wan22_e20_low.safetensors", "pumpwan22e20", "Low")]
    [InlineData("scifi_wan_low_30 (1).safetensors", "scifiwan", "Low")]
    [InlineData("scifi_wan_high_30 (1).safetensors", "scifiwan", "High")]
    public void Classify_ReturnsExpectedNormalizationAndLabel(string fileName, string expectedKey, string expectedLabel)
    {
        var model = new ModelClass
        {
            SafeTensorFileName = fileName,
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var result = LoraVariantClassifier.Classify(model);

        result.NormalizedKey.Should().Be(expectedKey);
        result.VariantLabel.Should().Be(expectedLabel);
    }
}
