using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The detail panel's category row (spec §4.4). It used to carry its own copy of the tag
/// inference — a copy that predated the <c>LooksLikeCategoryName</c> guard, so the real
/// Civitai tag "2000" showed up as a category literally called "2000", "5" as Clothing and
/// "character,style" as Celebrity (1|2 == 3). The panel now delegates to the one resolver,
/// so these rows mirror <c>SorterCategoryResolverTests</c>.
/// </summary>
public class ModelDetailViewModelCategoryTests
{
    private static Model ModelWithTags(params string[] tagNames)
    {
        var model = new Model { Name = "Test" };
        foreach (var name in tagNames)
        {
            model.Tags.Add(new ModelTag { Tag = new Tag { Name = name, NormalizedName = name.ToLowerInvariant() } });
        }

        return model;
    }

    [Theory]
    [InlineData("2000")]
    [InlineData("5")]
    [InlineData("character,style")]
    public void NumericAndCommaTagsProduceNoCategory(string tagName)
    {
        var (display, has) = ModelDetailViewModel.ComputeCategoryDisplay(ModelWithTags(tagName));

        has.Should().BeFalse($"'{tagName}' is not a category name — Enum.TryParse's numeric/flags forms must not leak through");
        display.Should().BeEmpty();
    }

    [Fact]
    public void FirstTagNamingACategoryWins()
    {
        var (display, has) = ModelDetailViewModel.ComputeCategoryDisplay(ModelWithTags("anime", "Character"));

        has.Should().BeTrue();
        display.Should().Be("Character");
    }

    [Fact]
    public void NoMatchingTagProducesNoCategory()
    {
        var (display, has) = ModelDetailViewModel.ComputeCategoryDisplay(ModelWithTags("anime", "photorealistic"));

        has.Should().BeFalse();
        display.Should().BeEmpty();
    }

    [Fact]
    public void UserCategoryOverrideWinsOverTags()
    {
        var model = ModelWithTags("Character");
        model.UserCategory = CivitaiCategory.Style;

        var (display, has) = ModelDetailViewModel.ComputeCategoryDisplay(model);

        has.Should().BeTrue();
        display.Should().Be("Style");
    }

    [Fact]
    public void BaseModelUserCategoryUsesTheSpacedDisplayForm()
    {
        var model = ModelWithTags();
        model.UserCategory = CivitaiCategory.BaseModel;

        var (display, has) = ModelDetailViewModel.ComputeCategoryDisplay(model);

        has.Should().BeTrue();
        display.Should().Be("Base Model");
    }

    [Fact]
    public void UndefinedStoredUserCategoryFallsThroughToTags()
    {
        // Corrupt/legacy stored value: it must not name the row by its raw number.
        var model = ModelWithTags("Style");
        model.UserCategory = (CivitaiCategory)2000;

        var (display, has) = ModelDetailViewModel.ComputeCategoryDisplay(model);

        has.Should().BeTrue();
        display.Should().Be("Style");
    }

    [Fact]
    public void NullModelProducesNoCategory()
    {
        var (display, has) = ModelDetailViewModel.ComputeCategoryDisplay(null);

        has.Should().BeFalse();
        display.Should().BeEmpty();
    }
}
