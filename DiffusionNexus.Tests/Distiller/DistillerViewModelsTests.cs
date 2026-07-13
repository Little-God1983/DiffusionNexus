using System.Linq;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Models.Distiller;
using DiffusionNexus.UI.ViewModels.Pipelines;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class DistillerViewModelsTests
{
    [Fact]
    public void RuleSet_delete_parses_words_from_commas_and_newlines()
    {
        var vm = new PromptRuleSetViewModel { Name = "Q", IsReplace = false, WordsText = "masterpiece, best quality\n4k" };

        var model = vm.ToModel();

        model.Kind.Should().Be(RuleKind.Delete);
        model.DeleteWords.Should().Equal("masterpiece", "best quality", "4k");
    }

    [Fact]
    public void RuleSet_replace_parses_arrow_pairs()
    {
        var vm = new PromptRuleSetViewModel { Name = "R", IsReplace = true, WordsText = "1girl => woman\n1boy -> man" };

        var model = vm.ToModel();

        model.Kind.Should().Be(RuleKind.Replace);
        model.ReplacePairs.Select(p => (p.From, p.To)).Should().Equal(("1girl", "woman"), ("1boy", "man"));
    }

    [Fact]
    public void Item_builds_edited_data_and_included_loras()
    {
        var data = new ImageGenerationData
        {
            PositivePrompt = "p", NegativePrompt = "n", Steps = 20, Cfg = 7, Seed = 5,
            SamplerName = "euler", Scheduler = "normal", Checkpoint = "base",
            Loras = [new LoraInfo { Name = "keep", StrengthModel = 0.8 }, new LoraInfo { Name = "drop", StrengthModel = 0.5 }],
            Width = 512, Height = 512, HasData = true
        };
        var item = new DistillerItemViewModel("c:/x.png", data);
        item.StepsText = "28";                 // user edit
        item.Loras.First(l => l.Name == "drop").Include = false;

        var edited = item.BuildEditedData();
        var loras = item.IncludedLoras();

        edited.Steps.Should().Be(28);
        edited.Checkpoint.Should().Be("base");
        loras.Select(l => l.Name).Should().Equal("keep");
    }
}
