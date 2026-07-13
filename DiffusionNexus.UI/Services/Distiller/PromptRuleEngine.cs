using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using DiffusionNexus.UI.Models.Distiller;

namespace DiffusionNexus.UI.Services.Distiller;

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

        // 1. Pull LoRA tokens out so rules can't touch them.
        var tokens = new List<string>();
        var body = LoraToken.Replace(prompt, m => { tokens.Add(m.Value); return ""; });

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
        body = Tidy(body.Replace("", " ").Trim());
        if (tokens.Count == 0) return body;

        var sb = new StringBuilder(body);
        foreach (var t in tokens)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
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
