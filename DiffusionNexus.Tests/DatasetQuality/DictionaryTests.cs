using DiffusionNexus.Service.Services.DatasetQuality.Dictionaries;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Structural tests for the data dictionaries used by dataset quality checks.
/// Verifies internal consistency rather than specific content.
/// </summary>
public class DictionaryTests
{
    #region StopWords

    [Fact]
    public void StopWordsSetIsNotEmpty()
    {
        StopWords.Set.Should().NotBeEmpty();
    }

    [Fact]
    public void StopWordsSetIsCaseInsensitive()
    {
        StopWords.Set.Contains("the").Should().BeTrue();
        StopWords.Set.Contains("THE").Should().BeTrue();
        StopWords.Set.Contains("The").Should().BeTrue();
    }

    [Fact]
    public void StopWordsContainsCommonArticles()
    {
        StopWords.Set.Should().Contain("a");
        StopWords.Set.Should().Contain("an");
        StopWords.Set.Should().Contain("the");
    }

    #endregion

    #region SynonymGroups

    [Fact]
    public void SynonymGroupsIsNotEmpty()
    {
        SynonymGroups.Groups.Should().NotBeEmpty();
    }

    [Fact]
    public void EverySynonymGroupHasAtLeastTwoTerms()
    {
        foreach (var group in SynonymGroups.Groups)
        {
            group.Count.Should().BeGreaterThanOrEqualTo(2,
                $"Synonym group should have ≥2 terms but found: {string.Join(", ", group)}");
        }
    }

    [Fact]
    public void SynonymGroupsHaveNoDuplicateTermsAcrossGroups()
    {
        var allTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<string>();

        foreach (var group in SynonymGroups.Groups)
        {
            foreach (var term in group)
            {
                if (!allTerms.Add(term))
                    duplicates.Add(term);
            }
        }

        duplicates.Should().BeEmpty(
            $"Terms appearing in multiple synonym groups: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void TermToGroupIndexCoversAllTerms()
    {
        var expectedCount = SynonymGroups.Groups.Sum(g => g.Count);

        SynonymGroups.TermToGroupIndex.Count.Should().Be(expectedCount);
    }

    [Fact]
    public void TermToGroupIndexPointsBackToCorrectGroup()
    {
        foreach (var (term, groupIndex) in SynonymGroups.TermToGroupIndex)
        {
            SynonymGroups.Groups[groupIndex].Should().Contain(term);
        }
    }

    #endregion

    #region FeatureCategories

    [Fact]
    public void FeatureCategoriesIsNotEmpty()
    {
        FeatureCategories.Categories.Should().NotBeEmpty();
    }

    [Fact]
    public void EveryCategoryHasAtLeastOneTerms()
    {
        foreach (var (category, terms) in FeatureCategories.Categories)
        {
            terms.Should().NotBeEmpty($"Category '{category}' should have terms");
        }
    }

    [Fact]
    public void FeatureCategoriesHaveNoEmptyTerms()
    {
        foreach (var (category, terms) in FeatureCategories.Categories)
        {
            terms.Should().AllSatisfy(t =>
                t.Should().NotBeNullOrWhiteSpace($"Category '{category}' has blank term"));
        }
    }

    [Fact]
    public void TermToCategoryCoversAllTerms()
    {
        var expectedCount = FeatureCategories.Categories.Values.Sum(v => v.Count);

        // Could be less if there are cross-category duplicates (which TryAdd skips)
        FeatureCategories.TermToCategory.Count.Should().BeLessThanOrEqualTo(expectedCount);
        FeatureCategories.TermToCategory.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TermToCategoryPointsBackToValidCategory()
    {
        foreach (var (term, category) in FeatureCategories.TermToCategory)
        {
            FeatureCategories.Categories.Should().ContainKey(category);
            FeatureCategories.Categories[category].Should().Contain(term);
        }
    }

    [Fact]
    public void CategoryNamesMatchCategoriesKeys()
    {
        FeatureCategories.CategoryNames.Should().BeEquivalentTo(FeatureCategories.Categories.Keys);
    }

    [Theory]
    [InlineData("hair_color")]
    [InlineData("eye_color")]
    [InlineData("pose")]
    [InlineData("clothing_upper")]
    [InlineData("setting_outdoor")]
    [InlineData("lighting")]
    public void WhenExpectedCategoryExistsThenItIsPresent(string category)
    {
        FeatureCategories.Categories.Should().ContainKey(category);
    }

    #endregion

    #region StyleLeakWords

    [Fact]
    public void StyleLeakWordsSetIsNotEmpty()
    {
        StyleLeakWords.Set.Should().NotBeEmpty();
    }

    [Fact]
    public void StyleLeakWordsSetIsCaseInsensitive()
    {
        StyleLeakWords.Set.Contains("masterpiece").Should().BeTrue();
        StyleLeakWords.Set.Contains("MASTERPIECE").Should().BeTrue();
    }

    [Fact]
    public void CriticalForStyleIsSubsetOfFullSet()
    {
        foreach (var term in StyleLeakWords.CriticalForStyle)
        {
            StyleLeakWords.Set.Should().Contain(term,
                $"CriticalForStyle term '{term}' must also be in the main Set");
        }
    }

    [Fact]
    public void CriticalForStyleIsNotEmpty()
    {
        StyleLeakWords.CriticalForStyle.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("masterpiece")]
    [InlineData("anime")]
    [InlineData("digital art")]
    [InlineData("oil painting")]
    [InlineData("stable diffusion")]
    public void WhenKnownStyleTermThenItIsInSet(string term)
    {
        StyleLeakWords.Set.Should().Contain(term);
    }

    #endregion
}
