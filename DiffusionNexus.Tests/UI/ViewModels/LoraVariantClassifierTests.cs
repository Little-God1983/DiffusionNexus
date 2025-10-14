using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiffusionNexus.Tests.UI.ViewModels;

public class LoraVariantClassifierTests
{
    [Theory]
    [MemberData(nameof(GetClassificationSamples))]
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

    public static IEnumerable<object[]> GetClassificationSamples()
    {
        yield return new object[] { "wriggling_t2v_high_e100.safetensors", "wrigglingt2v", "High" };
        yield return new object[] { "wriggling_t2v_low_e100.safetensors", "wrigglingt2v", "Low" };
        yield return new object[] { "WANTT2VHIGHNOISEJIGGLE", "wantt2vjiggle", "High" };
        yield return new object[] { "WANTT2VLOWNOISEJIGGLE", "wantt2vjiggle", "Low" };
        yield return new object[] { "Pump_wan22_e20_high.safetensors", "pumpwan22", "High" };
        yield return new object[] { "Pump_wan22_e20_low.safetensors", "pumpwan22", "Low" };
        yield return new object[] { "scifi_wan_low_30 (1).safetensors", "scifiwan", "Low" };
        yield return new object[] { "scifi_wan_high_30 (1).safetensors", "scifiwan", "High" };
        yield return new object[] { "wriggling_i2v_high_e010.safetensors", "wrigglingi2v", "High" };
        yield return new object[] { "wriggling_i2v_low_e020.safetensors", "wrigglingi2v", "Low" };
        yield return new object[] { "wan22-f4c3spl4sh-100epoc-high-k3nk.safetensors", "wan22f4c3spl4shk3nk", "High" };
        yield return new object[] { "wan22-f4c3spl4sh-154epoc-low-k3nk.safetensors", "wan22f4c3spl4shk3nk", "Low" };
        yield return new object[] { "model_HN.safetensors", "model", "High" };
        yield return new object[] { "model_LN.safetensors", "model", "Low" };
        yield return new object[] { "WAN-2.2-I2V-BPlay-HIGH-v1.safetensors", "wan22i2vbplay", "High" };
        yield return new object[] { "WAN-2.2-I2V-BPlay-LOW-v1.safetensors", "wan22i2vbplay", "Low" };
        yield return new object[] { "WAN-2.2-T2V-oggy Style-HIGH 14B.safetensors", "wan22t2voggystyle", "High" };
        yield return new object[] { "WAN-2.2-T2V-oggy Style-LOW 14B.safetensors", "wan22t2voggystyle", "Low" };
        yield return new object[] { "WAN-2.2-T2V-cial-HIGH 14B.safetensors", "wan22t2vcial", "High" };
        yield return new object[] { "WAN-2.2-T2V-cial-LOW 14B.safetensors", "wan22t2vcial", "Low" };
        yield return new object[] { "CassHamadaWan2.2HighNoise.safetensors", "casshamadawan2", "High" };
        yield return new object[] { "CassHamadaWan2.2HighNoise", "casshamadawan2", "High" };
        yield return new object[] { "CassHamadaWan2.2LowNoise.safetensors", "casshamadawan2", "Low" };
        yield return new object[] { "CassHamadaWan2.2LowNoise", "casshamadawan2", "Low" };
        yield return new object[] { "AAG_MuscleMommyH_high_noise.safetensors", "aagmusclemommy", "High" };
        yield return new object[] { "AAG_MuscleMommyL_low_noise.safetensors", "aagmusclemommy", "Low" };
        yield return new object[] { "wan2.2_highnoise_cshot_v.1.0.safetensors", "wan22cshot", "High" };
        yield return new object[] { "wan2.2_lownoise_cshot_v1.0.safetensors", "wan22cshot", "Low" };
        yield return new object[] { "WAN-2.2-I2V-BPlay-HIGH-v1", "wan22i2vbplay", "High" };
        yield return new object[] { "WAN-2.2-I2V-BPlay-LOW-v1", "wan22i2vbplay", "Low" };
        yield return new object[] { "Wan2.2 - I2V - King Machine - HIGH 14B.safetensors", "wan22i2vkingmachine", "High" };
        yield return new object[] { "Wan2.2 - I2V - King Machine - LOW 14B.safetensors", "wan22i2vkingmachine", "Low" };
        yield return new object[] { "WAN-2.2-T2V-oggy Style-HIGH 14B", "wan22t2voggystyle", "High" };
        yield return new object[] { "WAN-2.2-T2V-oggy Style-LOW 14B", "wan22t2voggystyle", "Low" };
        yield return new object[] { "WAN_2.2_mix_HIGH (Final).safetensors", "wan22mixfinal", "High" };
        yield return new object[] { "WAN_2.2_mix_LOW (Final).safetensors", "wan22mixfinal", "Low" };
        yield return new object[] { "wan - custom - LN 15B.safetensors", "wancustom", "Low" };
        yield return new object[] { "wan - custom - HN 15B.safetensors", "wancustom", "High" };
        yield return new object[] { "Another Model (LOW Noise).safetensors", "anothermodel", "Low" };
        yield return new object[] { "Another Model (HIGH Noise).safetensors", "anothermodel", "High" };
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

    [Fact]
    public void Classify_FallsBackToModelVersionNameWhenSafeTensorMissing()
    {
        var high = new ModelClass
        {
            SafeTensorFileName = string.Empty,
            ModelVersionName = "CassHamadaWan2.2HighNoise",
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var low = new ModelClass
        {
            SafeTensorFileName = null!,
            ModelVersionName = "CassHamadaWan2.2LowNoise",
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var highResult = LoraVariantClassifier.Classify(high);
        var lowResult = LoraVariantClassifier.Classify(low);

        highResult.VariantLabel.Should().Be("High");
        lowResult.VariantLabel.Should().Be("Low");
        highResult.NormalizedKey.Should().Be(lowResult.NormalizedKey);
    }

    [Fact]
    public void Classify_ReturnsEmptyKeyWhenOnlyNoisePresent()
    {
        var model = new ModelClass
        {
            SafeTensorFileName = "HighNoise",
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var result = LoraVariantClassifier.Classify(model);

        result.NormalizedKey.Should().BeEmpty();
        result.VariantLabel.Should().Be("High");
    }

    [Fact]
    public void Classify_SnapshotProtectsAgainstRegression()
    {
        var samples = new[]
        {
            "wan2.2_highnoise_cshot_v1.0 (Final Copy).safetensors",
            "wan2.2-lownoise-cshot-v1.0-final.safetensors",
            "WAN2.2_FINAL-HIGHNoise   .safetensors",
            "WAN2.2_FINAL-LowNoise   .safetensors",
            "Some Random Model.safetensors"
        };

        var snapshot = samples
            .Select(sample =>
            {
                var model = new ModelClass
                {
                    SafeTensorFileName = sample,
                    AssociatedFilesInfo = new List<FileInfo>()
                };

                var classification = LoraVariantClassifier.Classify(model);
                return (sample, classification);
            })
            .ToDictionary(
                entry => entry.sample,
                entry => ($"{entry.classification.NormalizedKey}|{entry.classification.VariantLabel}"));

        snapshot.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["wan2.2_highnoise_cshot_v1.0 (Final Copy).safetensors"] = "wan22cshot0finalcopy|High",
            ["wan2.2-lownoise-cshot-v1.0-final.safetensors"] = "wan22cshot0final|Low",
            ["WAN2.2_FINAL-HIGHNoise   .safetensors"] = "wan22final|High",
            ["WAN2.2_FINAL-LowNoise   .safetensors"] = "wan22fina|Low",
            ["Some Random Model.safetensors"] = "somerandommodel|"
        });
    }

    [Fact]
    public void Classify_UsesModelVersionVariantWhenFileNameLacksVariant()
    {
        var model = new ModelClass
        {
            SafeTensorFileName = "wan_cshot_v1.safetensors",
            ModelVersionName = "wan2.2_highnoise_cshot_v1.0",
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var result = LoraVariantClassifier.Classify(model);

        result.NormalizedKey.Should().Be("wancshot");
        result.VariantLabel.Should().Be("High");
    }

    [Fact]
    public void Merge_GroupsVariantsWithMatchingModelIdAndBaseModel()
    {
        var seeds = new List<LoraCardSeed>
        {
            CreateSeed("WAN-2.2-I2V-BPlay-HIGH-v1.safetensors", "1", "Wan Video 2.2"),
            CreateSeed("WAN-2.2-I2V-BPlay-LOW-v1.safetensors", "1", "Wan Video 2.2")
        };

        var entries = LoraVariantMerger.Merge(seeds);

        entries.Should().HaveCount(1);
        var entry = entries.Single();
        entry.Variants.Should().HaveCount(2);
        entry.Variants.Select(v => v.Label).Should().Contain(new[] { "High", "Low" });
        entry.Model.SafeTensorFileName.Should().Be("WAN-2.2-I2V-BPlay-HIGH-v1.safetensors");
    }

    [Fact]
    public void Merge_OrdersVariantsDeterministically()
    {
        var seeds = new List<LoraCardSeed>
        {
            CreateSeed("WAN-2.2-I2V-BPlay-LOW-v1.safetensors", "1", "Wan Video 2.2"),
            CreateSeed("WAN-2.2-I2V-BPlay-HIGH-v1.safetensors", "1", "Wan Video 2.2"),
        };

        var entries = LoraVariantMerger.Merge(seeds);

        entries.Should().HaveCount(1);
        var entry = entries.Single();
        entry.Variants.Select(v => v.Label).Should().ContainInOrder("High", "Low");
        entry.Variants[0].Model.SafeTensorFileName.Should().Contain("HIGH");
    }

    [Fact]
    public void Merge_DoesNotCombineWhenModelIdDiffers()
    {
        var seeds = new List<LoraCardSeed>
        {
            CreateSeed("WAN-2.2-I2V-BPlay-HIGH-v1.safetensors", "1", "Wan Video 2.2"),
            CreateSeed("WAN-2.2-I2V-BPlay-LOW-v1.safetensors", "2", "Wan Video 2.2")
        };

        var entries = LoraVariantMerger.Merge(seeds);

        entries.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_DoesNotCombineWhenBaseModelDiffers()
    {
        var seeds = new List<LoraCardSeed>
        {
            CreateSeed("WAN-2.2-I2V-BPlay-HIGH-v1.safetensors", "1", "Wan Video 2.2"),
            CreateSeed("WAN-2.2-I2V-BPlay-LOW-v1.safetensors", "1", "Different Base")
        };

        var entries = LoraVariantMerger.Merge(seeds);

        entries.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_PrefersFirstSeedPaths()
    {
        var first = CreateSeed("WAN-2.2-I2V-BPlay-HIGH-v1.safetensors", "1", "Wan Video 2.2");
        var second = CreateSeed("WAN-2.2-I2V-BPlay-LOW-v1.safetensors", "1", "Wan Video 2.2");
        second = second with { FolderPath = "other", SourcePath = "otherSource", TreePath = "otherTree" };

        var entries = LoraVariantMerger.Merge(new[] { first, second });

        entries.Should().HaveCount(1);
        var entry = entries.Single();
        entry.FolderPath.Should().Be(first.FolderPath);
        entry.SourcePath.Should().Be(first.SourcePath);
        entry.TreePath.Should().Be(first.TreePath);
    }

    [Fact]
    public void Merge_IgnoresSeedsWithoutVariantLabel()
    {
        var single = new ModelClass
        {
            SafeTensorFileName = "wan2.2_cshot.safetensors",
            ModelId = "1",
            DiffusionBaseModel = "Wan Video 2.2",
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var seeds = new[]
        {
            new LoraCardSeed(single, "source", "folder", "tree", Array.Empty<string>())
        };

        var entries = LoraVariantMerger.Merge(seeds);

        entries.Should().HaveCount(1);
        entries.Single().Variants.Should().BeEmpty();
    }

    private static LoraCardSeed CreateSeed(string fileName, string modelId, string baseModel)
    {
        var model = new ModelClass
        {
            SafeTensorFileName = fileName,
            ModelId = modelId,
            DiffusionBaseModel = baseModel,
            AssociatedFilesInfo = new List<FileInfo>()
        };

        return new LoraCardSeed(model, "source", "folder", "tree", Array.Empty<string>());
    }
}
