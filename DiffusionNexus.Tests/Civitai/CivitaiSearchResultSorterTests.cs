using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// The Civitai REST API silently ignores sort= whenever query= is present
/// (results arrive in Meilisearch relevance order), so the browser re-sorts
/// fetched pages client-side. These tests pin the sort semantics.
/// </summary>
public class CivitaiSearchResultSorterTests
{
    private static CivitaiModel Model(
        int id,
        int downloads = 0,
        int thumbsUp = 0,
        params string?[] versionPublishedAt)
    {
        return new CivitaiModel
        {
            Id = id,
            Name = $"model-{id}",
            Stats = new CivitaiModelStats { DownloadCount = downloads, ThumbsUpCount = thumbsUp },
            ModelVersions = versionPublishedAt
                .Select(d => new CivitaiModelVersion
                {
                    PublishedAt = d is null ? null : DateTimeOffset.Parse(d)
                })
                .ToList()
        };
    }

    private static int[] Ids(IEnumerable<CivitaiModel> models) => models.Select(m => m.Id).ToArray();

    [Fact]
    public void Newest_OrdersByLatestVersionPublishedAtDescending()
    {
        var models = new[]
        {
            Model(1, versionPublishedAt: "2023-05-01T00:00:00Z"),
            Model(2, versionPublishedAt: "2026-07-16T00:00:00Z"),
            Model(3, versionPublishedAt: "2024-01-01T00:00:00Z")
        };

        var sorted = CivitaiSearchResultSorter.Sort(models, m => m, CivitaiModelSort.Newest);

        Ids(sorted).Should().Equal(2, 3, 1);
    }

    [Fact]
    public void Newest_UsesMaxPublishedAtAcrossAllVersions_NotJustTheFirst()
    {
        // Model 1's FIRST listed version is old, but a later entry is the newest
        // of the whole set — the sorter must consider every version.
        var models = new[]
        {
            Model(1, versionPublishedAt: new[] { "2023-01-01T00:00:00Z", "2026-07-16T00:00:00Z" }),
            Model(2, versionPublishedAt: "2025-01-01T00:00:00Z")
        };

        var sorted = CivitaiSearchResultSorter.Sort(models, m => m, CivitaiModelSort.Newest);

        Ids(sorted).Should().Equal(1, 2);
    }

    [Fact]
    public void Newest_ModelsWithoutAnyPublishedDate_SortLast()
    {
        var models = new[]
        {
            Model(1, versionPublishedAt: (string?)null),
            Model(2, versionPublishedAt: "2024-01-01T00:00:00Z"),
            Model(3) // no versions at all
        };

        var sorted = CivitaiSearchResultSorter.Sort(models, m => m, CivitaiModelSort.Newest);

        sorted[0].Id.Should().Be(2);
        Ids(sorted).Skip(1).Should().BeEquivalentTo([1, 3]);
    }

    [Fact]
    public void MostDownloaded_OrdersByDownloadCountDescending()
    {
        var models = new[]
        {
            Model(1, downloads: 10),
            Model(2, downloads: 5000),
            Model(3, downloads: 200)
        };

        var sorted = CivitaiSearchResultSorter.Sort(models, m => m, CivitaiModelSort.MostDownloaded);

        Ids(sorted).Should().Equal(2, 3, 1);
    }

    [Fact]
    public void HighestRated_OrdersByThumbsUpCountDescending()
    {
        var models = new[]
        {
            Model(1, thumbsUp: 3),
            Model(2, thumbsUp: 900),
            Model(3, thumbsUp: 42)
        };

        var sorted = CivitaiSearchResultSorter.Sort(models, m => m, CivitaiModelSort.HighestRated);

        Ids(sorted).Should().Equal(2, 3, 1);
    }

    [Fact]
    public void UnknownSort_PreservesIncomingOrder()
    {
        var models = new[]
        {
            Model(1, versionPublishedAt: "2023-01-01T00:00:00Z"),
            Model(2, versionPublishedAt: "2026-01-01T00:00:00Z")
        };

        var sorted = CivitaiSearchResultSorter.Sort(models, m => m, "Relevancy");

        Ids(sorted).Should().Equal(1, 2);
    }

    [Fact]
    public void EqualKeys_PreserveIncomingRelevanceOrder()
    {
        // Stable sort: items the key can't distinguish keep Meilisearch's
        // relevance order instead of being shuffled.
        var models = new[]
        {
            Model(1, downloads: 100),
            Model(2, downloads: 100),
            Model(3, downloads: 100)
        };

        var sorted = CivitaiSearchResultSorter.Sort(models, m => m, CivitaiModelSort.MostDownloaded);

        Ids(sorted).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ApplyOrder_ReordersCollectionInPlace_PreservingInstances()
    {
        var a = Model(1);
        var b = Model(2);
        var c = Model(3);
        var collection = new System.Collections.ObjectModel.ObservableCollection<CivitaiModel> { a, b, c };

        CivitaiSearchResultSorter.ApplyOrder(collection, new[] { c, a, b });

        collection.Should().Equal(c, a, b);
    }

    [Fact]
    public void ApplyOrder_AlreadyOrdered_RaisesNoCollectionChanges()
    {
        var a = Model(1);
        var b = Model(2);
        var collection = new System.Collections.ObjectModel.ObservableCollection<CivitaiModel> { a, b };
        var changes = 0;
        collection.CollectionChanged += (_, _) => changes++;

        CivitaiSearchResultSorter.ApplyOrder(collection, new[] { a, b });

        changes.Should().Be(0);
    }

    [Fact]
    public void NullModels_SortLast_WithoutThrowing()
    {
        // The browser's design-time sample card has no CivitaiModel behind it.
        var items = new (int Tag, CivitaiModel? Model)[]
        {
            (1, null),
            (2, Model(2, versionPublishedAt: "2024-01-01T00:00:00Z"))
        };

        var sorted = CivitaiSearchResultSorter.Sort(items, i => i.Model, CivitaiModelSort.Newest);

        sorted.Select(i => i.Tag).Should().Equal(2, 1);
    }
}
