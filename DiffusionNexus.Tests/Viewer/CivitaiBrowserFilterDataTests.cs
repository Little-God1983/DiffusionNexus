using System.Text.Json;
using DiffusionNexus.UI.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the forward-compat contract of <see cref="CivitaiBrowserFilterData"/>'s four
/// show-flag bools (Installed/Early Access/Paywalled/NSFW): they must deserialize to
/// null — not the CLR <c>bool</c> default of <c>false</c> — when the persisted JSON was
/// written before the flag existed (or omits it for any other reason), because the
/// consuming ViewModel treats null as "show" (the default), not "hide". This is the
/// same pattern <c>LoraViewerFilterData.SortField</c>/<c>SortDescending</c> already use.
/// </summary>
public sealed class CivitaiBrowserFilterDataTests
{
    [Fact]
    public void Deserialize_JsonMissingShowFlags_LeavesThemNull()
    {
        const string json = """{"SelectedBaseModels":["Illustrious","SDXL 1.0"]}""";

        var data = JsonSerializer.Deserialize<CivitaiBrowserFilterData>(json)!;

        data.SelectedBaseModels.Should().BeEquivalentTo("Illustrious", "SDXL 1.0");
        data.ShowInstalled.Should().BeNull("a pre-existing saved filter must not silently hide installed models");
        data.ShowEarlyAccess.Should().BeNull("a pre-existing saved filter must not silently hide Early Access models");
        data.ShowPaywalled.Should().BeNull("a pre-existing saved filter must not silently hide paywalled models");
        data.ShowNsfw.Should().BeNull("a pre-existing saved filter must not silently hide NSFW models");
    }

    [Fact]
    public void Deserialize_EmptyJsonObject_DefaultsSelectedBaseModelsToEmptyList()
    {
        var data = JsonSerializer.Deserialize<CivitaiBrowserFilterData>("{}")!;

        data.SelectedBaseModels.Should().BeEmpty();
        data.ShowInstalled.Should().BeNull();
        data.ShowEarlyAccess.Should().BeNull();
        data.ShowPaywalled.Should().BeNull();
        data.ShowNsfw.Should().BeNull();
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTripsExplicitFalseFlags()
    {
        var data = new CivitaiBrowserFilterData
        {
            SelectedBaseModels = ["Krea 2"],
            ShowInstalled = false,
            ShowEarlyAccess = true,
            ShowPaywalled = false,
            ShowNsfw = true,
        };

        var json = JsonSerializer.Serialize(data);
        var restored = JsonSerializer.Deserialize<CivitaiBrowserFilterData>(json)!;

        restored.SelectedBaseModels.Should().BeEquivalentTo("Krea 2");
        restored.ShowInstalled.Should().BeFalse();
        restored.ShowEarlyAccess.Should().BeTrue();
        restored.ShowPaywalled.Should().BeFalse();
        restored.ShowNsfw.Should().BeTrue();
    }
}
