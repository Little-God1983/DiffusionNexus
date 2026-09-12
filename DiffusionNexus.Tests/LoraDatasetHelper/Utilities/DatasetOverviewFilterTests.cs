using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Utilities;

/// <summary>
/// Unit tests for <see cref="DatasetOverviewFilter"/>: the pure overview filter that decides
/// which cards the Dataset Management overview shows, how many datasets and versions the
/// filters hide, and which per-card "NSFW version hidden" labels are set.
/// </summary>
public class DatasetOverviewFilterTests : IDisposable
{
    private readonly string _root;

    public DatasetOverviewFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"DatasetOverviewFilterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    #region Flattened view

    [Fact]
    public void Flattened_NsfwVersionHidden_SiblingCardsCarryHiddenVersionLabel()
    {
        var dataset = CreateDataset("Alpha", versions: 3, nsfw: new() { [3] = true }, currentVersion: 3);
        var group = Flatten(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: null, filterType: null, showNsfw: false);

        var shown = result.Groups.Single().Datasets;
        shown.Select(c => c.DisplayVersion).Should().Equal(1, 2);
        shown.Should().AllSatisfy(c => c.HiddenVersionCount.Should().Be(1));
        result.HiddenVersionCount.Should().Be(1);
        result.HiddenDatasetCount.Should().Be(0);
    }

    [Fact]
    public void Flattened_ShowNsfwOn_NoCardIsHiddenAndNoLabelIsSet()
    {
        var dataset = CreateDataset("Alpha", versions: 3, nsfw: new() { [3] = true }, currentVersion: 3);
        var group = Flatten(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: null, filterType: null, showNsfw: true);

        var shown = result.Groups.Single().Datasets;
        shown.Select(c => c.DisplayVersion).Should().Equal(1, 2, 3);
        shown.Should().AllSatisfy(c => c.HiddenVersionCount.Should().Be(0));
        result.HiddenVersionCount.Should().Be(0);
        result.HiddenDatasetCount.Should().Be(0);
    }

    [Fact]
    public void Flattened_AllVersionsNsfw_CountsAsOneHiddenDatasetNotThreeVersions()
    {
        var dataset = CreateDataset("Alpha", versions: 3, nsfw: new() { [1] = true, [2] = true, [3] = true }, currentVersion: 3);
        var group = Flatten(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: null, filterType: null, showNsfw: false);

        result.Groups.Should().BeEmpty();
        result.HiddenDatasetCount.Should().Be(1);
        result.HiddenVersionCount.Should().Be(0);
    }

    [Fact]
    public void Flattened_TextFilterHidesOneVersionByDescription_CountsAsHiddenVersion()
    {
        var dataset = CreateDataset("Alpha", versions: 2, nsfw: new(), currentVersion: 2);
        dataset.VersionDescriptions[1] = "sketches";
        dataset.VersionDescriptions[2] = "renders";
        var group = Flatten(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: "renders", filterType: null, showNsfw: false);

        result.Groups.Single().Datasets.Single().DisplayVersion.Should().Be(2);
        result.HiddenVersionCount.Should().Be(1);
        result.HiddenDatasetCount.Should().Be(0);
    }

    #endregion

    #region Collapsed view

    [Fact]
    public void Collapsed_LatestVersionNsfw_ShowsSafeSnapshotWithHiddenVersionLabel()
    {
        var dataset = CreateDataset("Alpha", versions: 3, nsfw: new() { [3] = true }, currentVersion: 3);
        var group = Group(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: null, filterType: null, showNsfw: false);

        var card = result.Groups.Single().Datasets.Single();
        card.IsVersionCard.Should().BeFalse();
        card.IsNsfw.Should().BeFalse();
        card.HiddenVersionCount.Should().Be(1);
        result.HiddenVersionCount.Should().Be(1);
        result.HiddenDatasetCount.Should().Be(0);
    }

    [Fact]
    public void Collapsed_CurrentVersionSafeButOlderNsfw_RealCardCarriesHiddenVersionLabel()
    {
        var dataset = CreateDataset("Alpha", versions: 3, nsfw: new() { [2] = true }, currentVersion: 3);
        var group = Group(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: null, filterType: null, showNsfw: false);

        var card = result.Groups.Single().Datasets.Single();
        card.Should().BeSameAs(dataset);
        card.HiddenVersionCount.Should().Be(1);
        result.HiddenVersionCount.Should().Be(1);
    }

    [Fact]
    public void Collapsed_ShowNsfwToggledBackOn_ClearsLabelOnRealCard()
    {
        var dataset = CreateDataset("Alpha", versions: 3, nsfw: new() { [2] = true }, currentVersion: 3);
        var group = Group(dataset);

        DatasetOverviewFilter.Apply([group], filterText: null, filterType: null, showNsfw: false);
        var result = DatasetOverviewFilter.Apply([group], filterText: null, filterType: null, showNsfw: true);

        result.Groups.Single().Datasets.Single().HiddenVersionCount.Should().Be(0);
        result.HiddenVersionCount.Should().Be(0);
    }

    [Fact]
    public void Collapsed_AllVersionsNsfw_CountsAsHiddenDataset()
    {
        var dataset = CreateDataset("Alpha", versions: 2, nsfw: new() { [1] = true, [2] = true }, currentVersion: 2);
        var group = Group(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: null, filterType: null, showNsfw: false);

        result.Groups.Should().BeEmpty();
        result.HiddenDatasetCount.Should().Be(1);
        result.HiddenVersionCount.Should().Be(0);
    }

    [Fact]
    public void Collapsed_SingleVersionSafeDataset_NoLabelNoCounts()
    {
        var dataset = CreateDataset("Alpha", versions: 1, nsfw: new(), currentVersion: 1);
        var group = Group(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: null, filterType: null, showNsfw: false);

        result.Groups.Single().Datasets.Single().HiddenVersionCount.Should().Be(0);
        result.HiddenVersionCount.Should().Be(0);
        result.HiddenDatasetCount.Should().Be(0);
    }

    #endregion

    #region Text / type filters and grouping

    [Fact]
    public void TextFilter_NoVersionMatches_CountsAsHiddenDataset()
    {
        var dataset = CreateDataset("Alpha", versions: 1, nsfw: new(), currentVersion: 1);
        var group = Group(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: "zzz", filterType: null, showNsfw: false);

        result.Groups.Should().BeEmpty();
        result.HiddenDatasetCount.Should().Be(1);
    }

    [Fact]
    public void TypeFilter_MismatchedType_CountsAsHiddenDataset()
    {
        var dataset = CreateDataset("Alpha", versions: 1, nsfw: new(), currentVersion: 1);
        dataset.Type = DatasetType.Image;
        var group = Group(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: null, filterType: DatasetType.Video, showNsfw: false);

        result.Groups.Should().BeEmpty();
        result.HiddenDatasetCount.Should().Be(1);
    }

    [Fact]
    public void TextFilter_MatchesNameCaseInsensitively()
    {
        var dataset = CreateDataset("Alpha", versions: 1, nsfw: new(), currentVersion: 1);
        var group = Group(dataset);

        var result = DatasetOverviewFilter.Apply([group], filterText: "ALP", filterType: null, showNsfw: false);

        result.Groups.Single().Datasets.Should().ContainSingle();
        result.HiddenDatasetCount.Should().Be(0);
    }

    [Fact]
    public void Apply_PreservesGroupIdentityAndDropsEmptyGroups()
    {
        var shown = CreateDataset("Alpha", versions: 1, nsfw: new(), currentVersion: 1);
        var hidden = CreateDataset("Beta", versions: 1, nsfw: new() { [1] = true }, currentVersion: 1);
        var first = new DatasetGroupViewModel { CategoryId = 7, Name = "Characters", Description = "desc", SortOrder = 1 };
        first.Datasets.Add(shown);
        var second = new DatasetGroupViewModel { CategoryId = 8, Name = "Styles", SortOrder = 2 };
        second.Datasets.Add(hidden);

        var result = DatasetOverviewFilter.Apply([first, second], filterText: null, filterType: null, showNsfw: false);

        var group = result.Groups.Single();
        group.CategoryId.Should().Be(7);
        group.Name.Should().Be("Characters");
        group.Description.Should().Be("desc");
        group.SortOrder.Should().Be(1);
        group.Datasets.Should().ContainSingle().Which.Should().BeSameAs(shown);
        result.HiddenDatasetCount.Should().Be(1);
    }

    #endregion

    #region Hidden text

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(1, 0, "1 dataset hidden")]
    [InlineData(2, 0, "2 datasets hidden")]
    [InlineData(0, 1, "1 version hidden")]
    [InlineData(0, 3, "3 versions hidden")]
    [InlineData(1, 2, "1 dataset, 2 versions hidden")]
    [InlineData(2, 1, "2 datasets, 1 version hidden")]
    public void HiddenText_DescribesDatasetsAndVersions(int datasets, int versions, string expected)
    {
        var result = new DatasetOverviewFilterResult([], datasets, versions);

        result.HiddenText.Should().Be(expected);
        result.HasHidden.Should().Be(datasets + versions > 0);
    }

    #endregion

    #region Helpers

    private DatasetCardViewModel CreateDataset(string name, int versions, Dictionary<int, bool> nsfw, int currentVersion)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, ".dataset"));
        for (var v = 1; v <= versions; v++)
        {
            Directory.CreateDirectory(Path.Combine(folder, $"V{v}"));
        }

        var card = new DatasetCardViewModel
        {
            Name = name,
            FolderPath = folder,
            IsVersionedStructure = true,
            TotalVersions = versions,
            VersionNsfwFlags = nsfw,
            CurrentVersion = currentVersion
        };
        card.IsNsfw = nsfw.GetValueOrDefault(currentVersion, false);
        return card;
    }

    private static DatasetGroupViewModel Group(params DatasetCardViewModel[] datasets)
    {
        var group = DatasetGroupViewModel.CreateUncategorized();
        foreach (var d in datasets)
        {
            group.Datasets.Add(d);
        }
        return group;
    }

    /// <summary>Mirrors the overview's flatten expansion: one card per version.</summary>
    private static DatasetGroupViewModel Flatten(DatasetCardViewModel dataset)
    {
        var group = DatasetGroupViewModel.CreateUncategorized();
        foreach (var version in dataset.GetAllVersionNumbers())
        {
            group.Datasets.Add(dataset.CreateVersionCard(version));
        }
        return group;
    }

    #endregion
}
