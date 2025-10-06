using DiffusionNexus.UI.Classes;
using DiffusionNexus.Service.Classes;
using FluentAssertions;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiffusionNexus.Tests.LoraSort.Classes;

public class LoraVariantClassifierTests
{
    [Theory]
    [InlineData("wriggling_t2v_high_e100.safetensors", "wrigglingt2v", "High")]
    [InlineData("wriggling_t2v_low_e100.safetensors", "wrigglingt2v", "Low")]
    [InlineData("WANTT2VHIGHNOISEJIGGLE", "wantt2vjiggle", "High")]
    [InlineData("WANTT2VLOWNOISEJIGGLE", "wantt2vjiggle", "Low")]
    [InlineData("Pump_wan22_e20_high.safetensors", "pumpwan22", "High")]
    [InlineData("Pump_wan22_e20_low.safetensors", "pumpwan22", "Low")]
    [InlineData("scifi_wan_low_30 (1).safetensors", "scifiwan", "Low")]
    [InlineData("scifi_wan_high_30 (1).safetensors", "scifiwan", "High")]
    [InlineData("wriggling_i2v_high_e010.safetensors", "wrigglingi2v", "High")]
    [InlineData("wriggling_i2v_low_e020.safetensors", "wrigglingi2v", "Low")]
    [InlineData("wan22-f4c3spl4sh-100epoc-high-k3nk.safetensors", "wan22f4c3spl4shk3nk", "High")]
    [InlineData("wan22-f4c3spl4sh-154epoc-low-k3nk.safetensors", "wan22f4c3spl4shk3nk", "Low")]
    [InlineData("model_HN.safetensors", "model", "High")]
    [InlineData("model_LN.safetensors", "model", "Low")]
    [InlineData("WAN-2.2-I2V-BPlay-HIGH-v1.safetensors", "wan22i2vbplay", "High")]
    [InlineData("WAN-2.2-I2V-BPlay-LOW-v1.safetensors", "wan22i2vbplay", "Low")]
    [InlineData("Wan2.2 - I2V - King Machine - HIGH 14B.safetensors", "wan22i2vkingmachine", "High")]
    [InlineData("Wan2.2 - I2V - King Machine - LOW 14B.safetensors", "wan22i2vkingmachine", "Low")]
    [InlineData("AAG_MuscleMommyH_high_noise.safetensors", "aagmusclemommy", "High")]
    [InlineData("AAG_MuscleMommyL_low_noise.safetensors", "aagmusclemommy", "Low")]
    [InlineData("wan2.2_highnoise_cshot_v.1.0.safetensors", "wan22cshot", "High")]
    [InlineData("wan2.2_lownoise_cshot_v1.0.safetensors", "wan22cshot", "Low")]
    [InlineData("WAN-2.2-T2V-oggy Style-HIGH 14B.safetensors", "wan22t2voggystyle", "High")]
    [InlineData("WAN-2.2-T2V-oggy Style-LOW 14B.safetensors", "wan22t2voggystyle", "Low")]
    [InlineData("WAN-2.2-T2V-cial-HIGH 14B.safetensors", "wan22t2vcial", "High")]
    [InlineData("WAN-2.2-T2V-cial-LOW 14B.safetensors", "wan22t2vcial", "Low")]
    [InlineData("CassHamadaWan2.2HighNoise.safetensors", "casshamadawan2", "High")]
    [InlineData("CassHamadaWan2.2LowNoise.safetensors", "casshamadawan2", "Low")]
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

    [Fact]
    public void Classify_DoesNotMergeDistinctWanDownloads()
    {
        var inputs = new[]
        {
            "wan2.2_5b_c0wg1rl_72_000002500.safetensors",
            "wan2.2_5b_cuflation_000003750.safetensors",
            "wan2.2_5b_rsc_000002500.safetensors",
            "wan2.1-i2v-480p-rsacp.safetensors",
            "Wan2.1_i2v_cuinmouth_v1_7epo.safetensors"
        };

        var keys = inputs
            .Select(fileName =>
            {
                var model = new ModelClass
                {
                    SafeTensorFileName = fileName,
                    AssociatedFilesInfo = new List<FileInfo>()
                };

                return LoraVariantClassifier.Classify(model).NormalizedKey;
            })
            .ToList();

        keys.Should().OnlyHaveUniqueItems();
    }
}
