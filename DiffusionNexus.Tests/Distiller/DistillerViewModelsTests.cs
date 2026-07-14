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
    public void RuleSet_replace_builds_pairs_from_two_field_rows()
    {
        var vm = new PromptRuleSetViewModel { Name = "R", IsReplace = true };
        vm.Pairs.Add(new ReplacePairViewModel { From = " 1girl ", To = "woman" });
        vm.Pairs.Add(new ReplacePairViewModel { From = "1boy", To = "man" });
        vm.Pairs.Add(new ReplacePairViewModel { From = "   ", To = "ignored" }); // empty search term is skipped

        var model = vm.ToModel();

        model.Kind.Should().Be(RuleKind.Replace);
        model.ReplacePairs.Select(p => (p.From, p.To)).Should().Equal(("1girl", "woman"), ("1boy", "man"));
    }

    [Fact]
    public void RuleSet_pair_rows_can_be_added_and_removed()
    {
        var vm = new PromptRuleSetViewModel { IsReplace = true };

        vm.AddPairCommand.Execute(null);
        vm.AddPairCommand.Execute(null);
        vm.Pairs.Should().HaveCount(2);

        vm.RemovePairCommand.Execute(vm.Pairs[0]);
        vm.Pairs.Should().HaveCount(1);
    }

    [Fact]
    public void Item_tracks_prompt_modification_and_undo_restores_original()
    {
        var data = new ImageGenerationData { PositivePrompt = "original pos", NegativePrompt = "original neg", HasData = true };
        var item = new DistillerItemViewModel("c:/x.png", data);

        item.IsPositiveModified.Should().BeFalse();

        item.Positive = "edited";
        item.IsPositiveModified.Should().BeTrue();

        item.UndoPositiveCommand.Execute(null);
        item.Positive.Should().Be("original pos");
        item.IsPositiveModified.Should().BeFalse();

        item.Negative = "changed";
        item.IsNegativeModified.Should().BeTrue();
        item.UndoNegativeCommand.Execute(null);
        item.Negative.Should().Be("original neg");
        item.IsNegativeModified.Should().BeFalse();
    }

    [Fact]
    public void Item_editing_a_prompt_clears_its_test_highlights()
    {
        var data = new ImageGenerationData { PositivePrompt = "a cat", HasData = true };
        var item = new DistillerItemViewModel("c:/x.png", data)
        {
            PositiveHighlights = [new DiffusionNexus.UI.Controls.TextHighlightRange(2, 3, DiffusionNexus.UI.Controls.TextHighlightKind.Removal)]
        };

        item.Positive = "a cat!"; // stale positions would misalign — must clear

        item.PositiveHighlights.Should().BeNull();
    }

    [Fact]
    public void RuleSet_ToData_FromData_round_trips_losslessly()
    {
        var vm = new PromptRuleSetViewModel { Name = "Mine", IsReplace = true, Enabled = false };
        vm.Pairs.Add(new ReplacePairViewModel { From = "1girl", To = "woman" });
        vm.Pairs.Add(new ReplacePairViewModel { From = "cloud", To = "" });

        var restored = PromptRuleSetViewModel.FromData(vm.ToData());

        restored.Name.Should().Be("Mine");
        restored.IsReplace.Should().BeTrue();
        restored.Enabled.Should().BeFalse();
        restored.Pairs.Select(p => (p.From, p.To)).Should().Equal(("1girl", "woman"), ("cloud", ""));
    }

    [Fact]
    public void RuleSet_data_survives_json_round_trip_preserving_delete_formatting()
    {
        var delete = new PromptRuleSetViewModel { Name = "D", IsReplace = false, WordsText = "masterpiece, best quality\n4k" };
        var replace = new PromptRuleSetViewModel { Name = "R", IsReplace = true };
        replace.Pairs.Add(new ReplacePairViewModel { From = "a", To = "b" });

        var json = System.Text.Json.JsonSerializer.Serialize(
            new[] { delete.ToData(), replace.ToData() });
        var restored = System.Text.Json.JsonSerializer.Deserialize<
            System.Collections.Generic.List<PromptRuleSetData>>(json)!;

        restored.Should().HaveCount(2);
        restored[0].WordsText.Should().Be("masterpiece, best quality\n4k"); // raw formatting kept
        restored[1].Pairs.Should().Equal(new ReplacePairData("a", "b"));
    }

    [Fact]
    public void RuleSet_raises_Changed_for_every_kind_of_edit()
    {
        var vm = new PromptRuleSetViewModel { IsReplace = true };
        var raised = 0;
        vm.Changed += (_, _) => raised++;

        vm.Name = "renamed";                                    // own property
        raised.Should().Be(1);

        vm.Pairs.Add(new ReplacePairViewModel());                // row added
        raised.Should().Be(2);

        vm.Pairs[0].From = "cat";                                // pair text edited
        raised.Should().Be(3);

        var pair = vm.Pairs[0];
        vm.Pairs.Remove(pair);                                   // row removed
        raised.Should().Be(4);

        pair.To = "detached";                                    // removed pair no longer reports
        raised.Should().Be(4);
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
