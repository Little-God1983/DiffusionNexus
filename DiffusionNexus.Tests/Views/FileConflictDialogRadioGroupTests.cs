using System.Text.RegularExpressions;
using DiffusionNexus.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Views;

/// <summary>
/// Guards the conflict comparer's resolution radio buttons against a named <c>GroupName</c>.
/// Avalonia scopes named radio groups to the whole window, so binding the group to the file name
/// fused every row that shared a name into one group: "Ignore all" on two same-named rows left
/// only one of them with a dot while the view model still held Ignore for both. Radio buttons
/// without a <c>GroupName</c> group by their parent panel, which is exactly one row.
/// </summary>
public class FileConflictDialogRadioGroupTests
{
    [Fact]
    public void ConflictRowRadioButtons_MustNotDeclareAGroupName()
    {
        var axaml = File.ReadAllText(Path.Combine(
            RepoRoot.Path, "DiffusionNexus.UI", "Views", "Dialogs", "FileConflictDialog.axaml"));

        var radioButtons = Regex.Matches(axaml, @"<RadioButton\b[^>]*>", RegexOptions.Singleline);
        radioButtons.Should().HaveCount(3, "one radio per resolution: Override, Rename, Ignore");

        foreach (Match radio in radioButtons)
        {
            radio.Value.Should().NotContain("GroupName",
                "a per-name GroupName spans the whole window and merges same-named rows into one group");
        }
    }

}
