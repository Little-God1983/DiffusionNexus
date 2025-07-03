using DiffusionNexus.Service.Search;
using Xunit;
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
        Assert.Equal(new[] { 0, 2 }, result);
    }

    [Fact]
    public void SuggestReturnsPrefixMatches()
    {
        var index = new SearchIndex();
        index.Build(new[] { "red car", "blue truck", "green car" });
        var sugg = index.Suggest("c", 10).ToList();
        Assert.Contains("car", sugg);
    }
}
