using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public void Simulate_counts_occurrences_per_rule_including_zero_hits()
    {
        var del = new PromptRuleSet { Name = "D", Kind = RuleKind.Delete, DeleteWords = ["cloud", "missing"] };
        var rep = new PromptRuleSet { Name = "R", Kind = RuleKind.Replace, ReplacePairs = [new("cat", "dog")] };

        var results = PromptRuleEngine.Simulate("cloud, a cat, Cloud, cat and CLOUD", [del, rep]);

        results.Should().HaveCount(3);
        results[0].Description.Should().Be("Delete \"cloud\"");
        results[0].Count.Should().Be(3);              // case-insensitive
        results[1].Count.Should().Be(0);              // zero-hit rules are still reported
        results[2].Description.Should().Be("Replace \"cat\" → \"dog\"");
        results[2].Count.Should().Be(2);
    }

    [Fact]
    public void Simulate_is_sequential_like_apply()
    {
        // First set replaces cat→dog; second set counts dogs — must see the produced dogs.
        var r1 = new PromptRuleSet { Name = "1", Kind = RuleKind.Replace, ReplacePairs = [new("cat", "dog")] };
        var r2 = new PromptRuleSet { Name = "2", Kind = RuleKind.Delete, DeleteWords = ["dog"] };

        var results = PromptRuleEngine.Simulate("a cat and a dog", [r1, r2]);

        results[0].Count.Should().Be(1); // cat→dog
        results[1].Count.Should().Be(2); // the original dog + the produced one
    }

    [Fact]
    public void FindConflicts_flags_terms_that_are_both_deleted_and_replace_search_terms()
    {
        var del = new PromptRuleSet { Name = "D", Kind = RuleKind.Delete, DeleteWords = ["cloud", "sky"] };
        var rep = new PromptRuleSet { Name = "R", Kind = RuleKind.Replace, ReplacePairs = [new("Cloud", "mist"), new("tree", "bush")] };

        var conflicts = PromptRuleEngine.FindConflicts([del, rep]);

        conflicts.Should().HaveCount(1); // case-insensitive: "cloud" vs "Cloud"; "tree" is fine
        conflicts[0].Term.Should().Be("Cloud");
        conflicts[0].DeleteSetName.Should().Be("D");
        conflicts[0].ReplaceSetName.Should().Be("R");
    }

    [Fact]
    public void FindConflicts_ignores_disabled_sets_and_reports_each_term_once()
    {
        var delOff = new PromptRuleSet { Name = "off", Kind = RuleKind.Delete, Enabled = false, DeleteWords = ["cat"] };
        var del = new PromptRuleSet { Name = "D", Kind = RuleKind.Delete, DeleteWords = ["dog"] };
        var rep1 = new PromptRuleSet { Name = "R1", Kind = RuleKind.Replace, ReplacePairs = [new("cat", "x"), new("dog", "wolf")] };
        var rep2 = new PromptRuleSet { Name = "R2", Kind = RuleKind.Replace, ReplacePairs = [new("dog", "puppy")] };

        var conflicts = PromptRuleEngine.FindConflicts([delOff, del, rep1, rep2]);

        conflicts.Should().HaveCount(1);            // "cat" ignored (delete set disabled); "dog" reported once
        conflicts[0].Term.Should().Be("dog");
        conflicts[0].ReplaceSetName.Should().Be("R1");
    }

    [Fact]
    public void FindConflicts_returns_empty_when_no_overlap()
    {
        var del = new PromptRuleSet { Name = "D", Kind = RuleKind.Delete, DeleteWords = ["cloud"] };
        var rep = new PromptRuleSet { Name = "R", Kind = RuleKind.Replace, ReplacePairs = [new("tree", "bush")] };

        PromptRuleEngine.FindConflicts([del, rep]).Should().BeEmpty();
    }

    [Fact]
    public void FindMatches_reports_spans_on_original_text_and_skips_lora_tokens()
    {
        var del = new PromptRuleSet { Kind = RuleKind.Delete, DeleteWords = ["style"] };
        var rep = new PromptRuleSet { Kind = RuleKind.Replace, ReplacePairs = [new("cat", "dog")] };
        const string prompt = "a cat, style <lora:style:0.8>";

        var matches = PromptRuleEngine.FindMatches(prompt, [del, rep]);

        matches.Should().HaveCount(2); // "style" inside the lora token is NOT matched
        var styleMatch = matches.Single(m => !m.IsReplace);
        prompt.Substring(styleMatch.Start, styleMatch.Length).Should().Be("style");
        styleMatch.Start.Should().Be(7);
        var catMatch = matches.Single(m => m.IsReplace);
        prompt.Substring(catMatch.Start, catMatch.Length).Should().Be("cat");
    }
}
