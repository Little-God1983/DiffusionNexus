using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DiffusionNexus.UI.Models.Distiller;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>One rule's effect in a dry run: which set it came from, what it does, how often it hit.</summary>
internal sealed record RuleTestResult(string SetName, string Description, int Count);

/// <summary>A whole-word match of a rule in the ORIGINAL prompt text, for editor highlighting.</summary>
internal readonly record struct PromptMatch(int Start, int Length, bool IsReplace);

/// <summary>
/// A term that appears BOTH in an enabled delete set and as an enabled replace search term —
/// whichever rule runs first starves the other (a deleted word can't be replaced, and vice versa).
/// </summary>
internal sealed record RuleConflict(string Term, string DeleteSetName, string ReplaceSetName);

/// <summary>
/// Applies delete/replace <see cref="PromptRuleSet"/>s to a prompt. LoRA tokens (&lt;lora:...&gt;) are
/// extracted before rules run and re-appended after, so a blacklist can never corrupt a LoRA name.
/// Matching is whole-word and case-insensitive.
/// </summary>
internal static class PromptRuleEngine
{
    private static readonly Regex LoraToken = new(@"<lora:[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Apply(string prompt, IReadOnlyList<PromptRuleSet> sets)
    {
        if (string.IsNullOrEmpty(prompt) || sets is null || sets.Count == 0)
            return prompt ?? string.Empty;

        // 1. Pull LoRA tokens out so rules can't touch them (replace with a space to keep word separation).
        var tokens = new List<string>();
        var body = LoraToken.Replace(prompt, m => { tokens.Add(m.Value); return " "; });

        // 2. Apply enabled sets in order.
        foreach (var set in sets)
        {
            if (!set.Enabled) continue;
            body = set.Kind switch
            {
                RuleKind.Delete => ApplyDelete(body, set.DeleteWords),
                RuleKind.Replace => ApplyReplace(body, set.ReplacePairs),
                _ => body,
            };
        }

        // 3. Tidy separators, then re-append the tokens.
        body = Tidy(body.Trim());
        if (tokens.Count == 0) return body;

        var sb = new StringBuilder(body);
        foreach (var t in tokens)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Dry-runs the enabled sets against <paramref name="prompt"/> exactly like <see cref="Apply"/>
    /// (sequentially, on the LoRA-stripped body) and reports how many occurrences each rule would
    /// hit. Rules with zero hits are included so the user sees them checked.
    /// </summary>
    public static IReadOnlyList<RuleTestResult> Simulate(string prompt, IReadOnlyList<PromptRuleSet> sets)
    {
        var results = new List<RuleTestResult>();
        if (sets is null || sets.Count == 0) return results;

        var body = LoraToken.Replace(prompt ?? string.Empty, " ");

        foreach (var set in sets)
        {
            if (!set.Enabled) continue;

            if (set.Kind == RuleKind.Delete)
            {
                foreach (var w in set.DeleteWords)
                {
                    if (string.IsNullOrWhiteSpace(w)) continue;
                    var pattern = $@"\b{Regex.Escape(w.Trim())}\b";
                    var count = Regex.Matches(body, pattern, RegexOptions.IgnoreCase).Count;
                    results.Add(new RuleTestResult(set.Name, $"Delete \"{w.Trim()}\"", count));
                    body = Regex.Replace(body, pattern, "", RegexOptions.IgnoreCase);
                }
            }
            else if (set.Kind == RuleKind.Replace)
            {
                foreach (var p in set.ReplacePairs)
                {
                    if (string.IsNullOrWhiteSpace(p.From)) continue;
                    var pattern = $@"\b{Regex.Escape(p.From.Trim())}\b";
                    var count = Regex.Matches(body, pattern, RegexOptions.IgnoreCase).Count;
                    results.Add(new RuleTestResult(set.Name, $"Replace \"{p.From.Trim()}\" → \"{p.To}\"", count));
                    body = Regex.Replace(body, pattern, p.To ?? "", RegexOptions.IgnoreCase);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Cross-checks enabled delete words against enabled replace search terms (case-insensitive,
    /// trimmed). A collision means the two rules fight over the same term: if the delete runs first
    /// the replace never matches; if the replace runs first the delete never matches.
    /// </summary>
    public static IReadOnlyList<RuleConflict> FindConflicts(IReadOnlyList<PromptRuleSet> sets)
    {
        var conflicts = new List<RuleConflict>();
        if (sets is null || sets.Count == 0) return conflicts;

        // First enabled delete set claiming a word wins the report (duplicates add no information).
        var deleteWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in sets)
        {
            if (!set.Enabled || set.Kind != RuleKind.Delete) continue;
            foreach (var w in set.DeleteWords)
                if (!string.IsNullOrWhiteSpace(w))
                    deleteWords.TryAdd(w.Trim(), set.Name);
        }
        if (deleteWords.Count == 0) return conflicts;

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in sets)
        {
            if (!set.Enabled || set.Kind != RuleKind.Replace) continue;
            foreach (var p in set.ReplacePairs)
            {
                if (string.IsNullOrWhiteSpace(p.From)) continue;
                var term = p.From.Trim();
                if (deleteWords.TryGetValue(term, out var deleteSet) && reported.Add(term))
                    conflicts.Add(new RuleConflict(term, deleteSet, set.Name));
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Finds every enabled rule's whole-word matches in the ORIGINAL prompt text (positions refer to
    /// the text as displayed in the editor), skipping matches inside &lt;lora:...&gt; tokens. Each rule
    /// matches against the original text — chained effects of earlier rules are not simulated here;
    /// use <see cref="Simulate"/> for exact sequential counts.
    /// </summary>
    public static IReadOnlyList<PromptMatch> FindMatches(string prompt, IReadOnlyList<PromptRuleSet> sets)
    {
        var matches = new List<PromptMatch>();
        if (string.IsNullOrEmpty(prompt) || sets is null || sets.Count == 0) return matches;

        // Spans occupied by LoRA tokens — rule matches inside them are ignored (Apply never sees them).
        var loraSpans = LoraToken.Matches(prompt).Select(m => (m.Index, m.Length)).ToList();

        foreach (var set in sets)
        {
            if (!set.Enabled) continue;

            if (set.Kind == RuleKind.Delete)
            {
                foreach (var w in set.DeleteWords)
                    AddMatches(prompt, w, isReplace: false, loraSpans, matches);
            }
            else if (set.Kind == RuleKind.Replace)
            {
                foreach (var p in set.ReplacePairs)
                    AddMatches(prompt, p.From, isReplace: true, loraSpans, matches);
            }
        }

        return matches;
    }

    private static void AddMatches(string prompt, string word, bool isReplace,
        List<(int Index, int Length)> loraSpans, List<PromptMatch> matches)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        foreach (Match m in Regex.Matches(prompt, $@"\b{Regex.Escape(word.Trim())}\b", RegexOptions.IgnoreCase))
        {
            var insideLora = loraSpans.Any(s => m.Index < s.Index + s.Length && m.Index + m.Length > s.Index);
            if (!insideLora)
                matches.Add(new PromptMatch(m.Index, m.Length, isReplace));
        }
    }

    private static string ApplyDelete(string text, IReadOnlyList<string> words)
    {
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w)) continue;
            text = Regex.Replace(text, $@"\b{Regex.Escape(w.Trim())}\b", "", RegexOptions.IgnoreCase);
        }
        return text;
    }

    private static string ApplyReplace(string text, IReadOnlyList<ReplacePair> pairs)
    {
        foreach (var p in pairs)
        {
            if (string.IsNullOrWhiteSpace(p.From)) continue;
            text = Regex.Replace(text, $@"\b{Regex.Escape(p.From.Trim())}\b", p.To ?? "", RegexOptions.IgnoreCase);
        }
        return text;
    }

    // Collapse the whitespace/commas left behind by deletions: "a , , b" -> "a, b".
    private static string Tidy(string text)
    {
        text = Regex.Replace(text, @"\s+", " ");
        text = Regex.Replace(text, @"\s*,\s*", ", ");
        text = Regex.Replace(text, @"(,\s*){2,}", ", ");
        text = text.Trim().Trim(',').Trim();
        return text;
    }
}
