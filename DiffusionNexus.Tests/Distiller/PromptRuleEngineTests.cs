using System.Collections.Generic;
using DiffusionNexus.UI.Models.Distiller;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class PromptRuleEngineTests
{
    [Fact]
    public void Delete_removes_whole_words_case_insensitively_and_tidies_commas()
    {
        var set = new PromptRuleSet { Kind = RuleKind.Delete, DeleteWords = ["masterpiece", "4k"] };

        var result = PromptRuleEngine.Apply("Masterpiece, a cat, 4k, detailed", [set]);

        result.Should().Be("a cat, detailed");
    }

    [Fact]
    public void Delete_does_not_remove_substrings_inside_other_words()
    {
        var set = new PromptRuleSet { Kind = RuleKind.Delete, DeleteWords = ["art"] };

        var result = PromptRuleEngine.Apply("art, cartoon, artist", [set]);

        result.Should().Be("cartoon, artist");
    }

    [Fact]
    public void Replace_substitutes_pairs_case_insensitively()
    {
        var set = new PromptRuleSet { Kind = RuleKind.Replace, ReplacePairs = [new("1girl", "woman"), new("1boy", "man")] };

        var result = PromptRuleEngine.Apply("1girl and 1BOY", [set]);

        result.Should().Be("woman and man");
    }

    [Fact]
    public void Lora_tokens_survive_delete_and_replace_and_are_reappended()
    {
        var del = new PromptRuleSet { Kind = RuleKind.Delete, DeleteWords = ["style"] };
        var rep = new PromptRuleSet { Kind = RuleKind.Replace, ReplacePairs = [new("cat", "dog")] };

        var result = PromptRuleEngine.Apply("a cat, style <lora:styleB:0.8>", [del, rep]);

        result.Should().Be("a dog <lora:styleB:0.8>");
    }

    [Fact]
    public void Disabled_sets_are_ignored()
    {
        var set = new PromptRuleSet { Kind = RuleKind.Delete, Enabled = false, DeleteWords = ["cat"] };

        var result = PromptRuleEngine.Apply("a cat", [set]);

        result.Should().Be("a cat");
    }
}
