using DiffusionNexus.Service.Search;
using Xunit;
using FluentAssertions;
using System.Linq;

namespace DiffusionNexus.Tests.LoraSort.Search;
public class SearchIndexTests
{
    [Fact]
    public void BuildAndSearchReturnsExpectedIndexes()
    {
        var index = new SearchIndex();
        index.Build(new[] { "red car", "blue truck", "green car" });
        var result = index.Search("car").ToList();
        result.Should().Equal(new[] { 0, 2 });
    }

    [Fact]
    public void SuggestReturnsPrefixMatches()
    {
        var index = new SearchIndex();
        index.Build(new[] { "red car", "blue truck", "green car" });
        var sugg = index.Suggest("c", 10).ToList();
        sugg.Should().Contain("car");
    }

    [Fact]
    public void SearchPrefixReturnsPartialMatches()
    {
        var index = new SearchIndex();
        index.Build(new[] { "Fright Night", "Halloween Nightmare", "Nightmare Fuel" });
        var result = index.SearchPrefix("night").OrderBy(i => i).ToList();
        result.Should().Equal(new[] { 0, 1, 2 });
    }
}
