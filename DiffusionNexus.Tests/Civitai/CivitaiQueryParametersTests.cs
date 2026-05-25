using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiQueryParametersTests
{
    private static string Build(CivitaiModelsQuery q)
    {
        var method = typeof(CivitaiModelsQuery).GetMethod("ToQueryString",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (string)method.Invoke(q, null)!;
    }

    private static string Build(CivitaiImagesQuery q)
    {
        var method = typeof(CivitaiImagesQuery).GetMethod("ToQueryString",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (string)method.Invoke(q, null)!;
    }

    [Fact]
    public void ModelsQuery_Empty_ProducesEmptyString()
    {
        Build(new CivitaiModelsQuery()).Should().BeEmpty();
    }

    [Fact]
    public void ModelsQuery_AllScalarFields_AreEmittedInOrder()
    {
        var q = new CivitaiModelsQuery
        {
            Limit = 10,
            Page = 2,
            Query = "anime girl",
            Tag = "style",
            Username = "alice",
            Sort = "Most Downloaded",
            Period = CivitaiPeriod.Month,
            Nsfw = "false",
            PrimaryFileOnly = true,
            BaseModel = "SDXL 1.0"
        };

        var result = Build(q);

        result.Should().Be(
            "limit=10&page=2&query=anime%20girl&tag=style&username=alice&sort=Most%20Downloaded&period=Month&nsfw=false&primaryFileOnly=true&baseModel=SDXL%201.0");
    }

    [Fact]
    public void ModelsQuery_Types_EmitsRepeatedTypesParameter()
    {
        var q = new CivitaiModelsQuery
        {
            Types = new[] { CivitaiModelType.LORA, CivitaiModelType.Checkpoint }
        };

        var result = Build(q);

        result.Should().Be("types=LORA&types=Checkpoint");
    }

    [Fact]
    public void ModelsQuery_NullAndWhitespaceStrings_AreOmitted()
    {
        var q = new CivitaiModelsQuery
        {
            Query = "   ",
            Tag = null,
            Username = "",
            BaseModel = "  "
        };

        Build(q).Should().BeEmpty();
    }

    [Fact]
    public void ModelsQuery_EmptyTypesList_IsOmitted()
    {
        var q = new CivitaiModelsQuery { Types = Array.Empty<CivitaiModelType>() };

        Build(q).Should().BeEmpty();
    }

    [Fact]
    public void ModelsQuery_PrimaryFileOnly_IsLowercase()
    {
        Build(new CivitaiModelsQuery { PrimaryFileOnly = true }).Should().Be("primaryFileOnly=true");
        Build(new CivitaiModelsQuery { PrimaryFileOnly = false }).Should().Be("primaryFileOnly=false");
    }

    [Fact]
    public void ModelsQuery_Nsfw_IsPassedThroughAsString()
    {
        // Civitai's API expects a string on the models endpoint, not a bool.
        Build(new CivitaiModelsQuery { Nsfw = "true" }).Should().Be("nsfw=true");
        Build(new CivitaiModelsQuery { Nsfw = "X" }).Should().Be("nsfw=X");
    }

    [Fact]
    public void ModelsQuery_Nsfw_NullOrWhitespace_IsOmitted()
    {
        Build(new CivitaiModelsQuery { Nsfw = null }).Should().BeEmpty();
        Build(new CivitaiModelsQuery { Nsfw = "   " }).Should().BeEmpty();
    }

    [Fact]
    public void ModelsQuery_Cursor_IsEscapedAndEmittedEarly()
    {
        // Cursor is opaque — must be URL-escaped (it can contain '+' or '/').
        var q = new CivitaiModelsQuery { Limit = 5, Cursor = "abc+def/ghi" };

        Build(q).Should().Be("limit=5&cursor=abc%2Bdef%2Fghi");
    }

    [Fact]
    public void ModelsQuery_BaseModels_EmitsRepeatedBaseModelsParameter()
    {
        // REST endpoint takes repeated `baseModels=` (plural) — NOT comma-joined,
        // and NOT the singular `baseModel=` form the civitai.com web UI uses.
        var q = new CivitaiModelsQuery
        {
            BaseModels = new[] { "SDXL 1.0", "Pony" }
        };

        Build(q).Should().Be("baseModels=SDXL%201.0&baseModels=Pony");
    }

    [Fact]
    public void ModelsQuery_BaseModels_WhitespaceEntriesAreOmitted()
    {
        var q = new CivitaiModelsQuery
        {
            BaseModels = new[] { "SDXL 1.0", "", "   ", "Pony" }
        };

        Build(q).Should().Be("baseModels=SDXL%201.0&baseModels=Pony");
    }

    [Fact]
    public void ModelsQuery_EmptyBaseModelsList_IsOmitted()
    {
        var q = new CivitaiModelsQuery { BaseModels = Array.Empty<string>() };

        Build(q).Should().BeEmpty();
    }

    [Fact]
    public void ImagesQuery_Bool_IsLowercase()
    {
        // ImagesQuery.Nsfw is still bool? — only the Models endpoint requires a string.
        Build(new CivitaiImagesQuery { Nsfw = true }).Should().Be("nsfw=true");
        Build(new CivitaiImagesQuery { Nsfw = false }).Should().Be("nsfw=false");
    }

    [Fact]
    public void ImagesQuery_Empty_ProducesEmptyString()
    {
        Build(new CivitaiImagesQuery()).Should().BeEmpty();
    }

    [Fact]
    public void ImagesQuery_AllScalarFields_AreEmittedInOrder()
    {
        var q = new CivitaiImagesQuery
        {
            Limit = 50,
            Page = 1,
            PostId = 100,
            ModelId = 200,
            ModelVersionId = 300,
            Username = "bob",
            Nsfw = true,
            Sort = "Newest",
            Period = CivitaiPeriod.Day
        };

        var result = Build(q);

        result.Should().Be(
            "limit=50&page=1&postId=100&modelId=200&modelVersionId=300&username=bob&nsfw=true&sort=Newest&period=Day");
    }

    [Fact]
    public void ImagesQuery_OnlyOptionalsSet_OmitsTheRest()
    {
        var q = new CivitaiImagesQuery { ModelVersionId = 42 };

        Build(q).Should().Be("modelVersionId=42");
    }

    [Theory]
    [InlineData("a b", "query=a%20b")]
    [InlineData("foo&bar", "query=foo%26bar")]
    [InlineData("100% sure", "query=100%25%20sure")]
    public void ModelsQuery_Query_IsUriEscaped(string input, string expected)
    {
        Build(new CivitaiModelsQuery { Query = input }).Should().Be(expected);
    }
}
