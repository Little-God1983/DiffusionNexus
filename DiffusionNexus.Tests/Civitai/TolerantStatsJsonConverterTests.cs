using System.Text.Json;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Service.Classes.CivitaiModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Civitai returns JSON <c>null</c> for stats counters on freshly published models
/// (stats not yet computed server-side). All stats DTOs declared non-nullable
/// <c>int</c>/<c>double</c>, so one such model killed deserialization of the whole
/// 50-item page: "The JSON value could not be converted to System.Int32.
/// Path: $.items[45].stats.downloadCount" (user-reported — Civitai Browser with a
/// base-model filter and Newest sort, which surfaces minutes-old models). Stats are
/// informational only, so no stats shape may ever fail the surrounding payload:
/// numbers pass through, null/missing/other shapes read as zero.
/// </summary>
public class TolerantStatsJsonConverterTests
{
    [Fact]
    public void CivitaiModel_ReadsNullModelStatsCountersAsZero()
    {
        // Minimal form of the crashing shape at $.items[45].stats.
        var json = """
            {"id":1,"name":"fresh","stats":{"downloadCount":null,"favoriteCount":null,
             "commentCount":null,"ratingCount":null,"rating":null,"thumbsUpCount":null,
             "thumbsDownCount":null}}
            """;

        var model = JsonSerializer.Deserialize<CivitaiModel>(json);

        model!.Stats.Should().NotBeNull();
        model.Stats!.DownloadCount.Should().Be(0);
        model.Stats.FavoriteCount.Should().Be(0);
        model.Stats.CommentCount.Should().Be(0);
        model.Stats.RatingCount.Should().Be(0);
        model.Stats.Rating.Should().Be(0);
        model.Stats.ThumbsUpCount.Should().Be(0);
        model.Stats.ThumbsDownCount.Should().Be(0);
    }

    [Fact]
    public void CivitaiModelVersion_ReadsNullVersionStatsCountersAsZero()
    {
        var json = """
            {"id":2,"name":"v1","stats":{"downloadCount":null,"ratingCount":null,
             "rating":null,"thumbsUpCount":null,"thumbsDownCount":null}}
            """;

        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(json);

        version!.Stats.Should().NotBeNull();
        version.Stats!.DownloadCount.Should().Be(0);
        version.Stats.RatingCount.Should().Be(0);
        version.Stats.Rating.Should().Be(0);
        version.Stats.ThumbsUpCount.Should().Be(0);
        version.Stats.ThumbsDownCount.Should().Be(0);
    }

    [Fact]
    public void CivitaiModelImage_ReadsNullImageStatsCountersAsZero()
    {
        var json = """
            {"url":"https://example/x.jpeg","stats":{"cryCount":null,"laughCount":null,
             "likeCount":null,"dislikeCount":null,"heartCount":null,"commentCount":null}}
            """;

        var image = JsonSerializer.Deserialize<CivitaiModelImage>(json);

        image!.Stats.Should().NotBeNull();
        image.Stats!.CryCount.Should().Be(0);
        image.Stats.LaughCount.Should().Be(0);
        image.Stats.LikeCount.Should().Be(0);
        image.Stats.DislikeCount.Should().Be(0);
        image.Stats.HeartCount.Should().Be(0);
        image.Stats.CommentCount.Should().Be(0);
    }

    [Fact]
    public void CivitaiPagedResponse_SurvivesOneItemWithNullStats()
    {
        // The user-visible failure mode: 49 healthy models were thrown away because
        // one freshly published model carried null stats. The page must parse whole.
        var json = """
            {"items":[
              {"id":10,"name":"healthy","stats":{"downloadCount":16,"thumbsUpCount":1,"thumbsDownCount":0,"commentCount":0,"rating":4.5,"ratingCount":3,"favoriteCount":2}},
              {"id":11,"name":"fresh","stats":{"downloadCount":null,"thumbsUpCount":null,"thumbsDownCount":null,"commentCount":null,"rating":null,"ratingCount":null,"favoriteCount":null}}
            ],"metadata":{"nextCursor":"12"}}
            """;

        var page = JsonSerializer.Deserialize<CivitaiPagedResponse<CivitaiModel>>(json);

        page!.Items.Should().HaveCount(2);
        page.Items[0].Stats!.DownloadCount.Should().Be(16);
        page.Items[0].Stats!.Rating.Should().Be(4.5);
        page.Items[1].Stats!.DownloadCount.Should().Be(0);
    }

    [Fact]
    public void CivitaiModel_DeserializesTheRealFailingPagePayload()
    {
        // Condensed real payload from the failing request (models?baseModels=Krea 2
        // &sort=Newest, items[0] "Pocahontas - Disney - Krea2 LORA", verbatim structure
        // with description and image list shortened) followed by a reconstruction of
        // the truncated items[45]: a minutes-old model whose stats are still null.
        const string raw =
            """
            {"items":[
            {"id":2851602,"name":"Pocahontas - Disney - Krea2 LORA","description":"<p>Trained on Krea2.</p>","allowNoCredit":true,"allowCommercialUse":["Image","RentCivit"],"allowDerivatives":true,"allowDifferentLicense":true,"type":"LORA","minor":false,"sfwOnly":false,"poi":false,"nsfw":false,"nsfwLevel":7,"availability":"Public","userId":137281,"baseModels":["Krea 2"],"cosmetic":null,"supportsGeneration":true,"stats":{"downloadCount":0,"thumbsUpCount":1,"thumbsDownCount":0,"commentCount":0,"tippedAmountCount":0},"creator":{"username":"Konan","image":"https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA/7cd552a1-60fe-4baf-a0e4-f7d5d5381711/width=96/Konan.jpeg"},"tags":["pocahontas","character","cartoon","woman","disney"],"modelVersions":[{"id":3220243,"index":0,"name":"v1.0","baseModel":"Krea 2","baseModelType":"Standard","publishedAt":"2026-08-11T21:25:04.626Z","flags":0,"availability":"Public","nsfwLevel":0,"description":null,"trainedWords":["pocahontas, a native american woman"],"vaeId":null,"paidAccess":null,"stats":{"downloadCount":2,"thumbsUpCount":1,"thumbsDownCount":0},"supportsGeneration":true,"files":[{"id":3102175,"sizeKB":223231.3515625,"name":"Pocahontas_Krea2_byKonan.safetensors","overrideName":null,"type":"Model","pickleScanResult":"Success","pickleScanMessage":null,"virusScanResult":"Success","virusScanMessage":null,"scannedAt":"2026-08-11T21:20:28.450Z","metadata":{"format":"SafeTensor","fp":"bf16","isRequired":false},"hashes":{"AutoV1":"A7290BE0","AutoV2":"96A7FB4ABC","SHA256":"96A7FB4ABC026B628EC2C28FB91086A5F5557AA14244EC329581B27CB53096CD","CRC32":"EEC03BA6","BLAKE3":"D4BB12C33C43866BAA1FB085A51CB3ED575972AEA5DED46FD3541F568F7FFC80","AutoV3":"9F02751CC929"},"downloadUrl":"https://civitai.com/api/download/models/3220243","primary":true}],"images":[{"id":139439234,"url":"https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA/a49ce65f-bee7-4f67-859a-9fd1f8381c3e/original=true/139439234.jpeg","nsfwLevel":2,"width":1776,"height":2368,"hash":"UAG*TdOr0m}[04t7~ANK7NX8_2X9x[M|WGtQ","type":"image","minor":false,"poi":false,"hasMeta":true,"hasPositivePrompt":true,"onSite":false,"remixOfId":null}]}]},
            {"id":2851700,"name":"Brand New Krea2 LORA","description":null,"allowNoCredit":true,"allowCommercialUse":[],"allowDerivatives":true,"allowDifferentLicense":true,"type":"LORA","minor":false,"sfwOnly":false,"poi":false,"nsfw":false,"nsfwLevel":1,"availability":"Public","userId":1,"baseModels":["Krea 2"],"cosmetic":null,"supportsGeneration":false,"stats":{"downloadCount":null,"thumbsUpCount":null,"thumbsDownCount":null,"commentCount":null,"tippedAmountCount":null},"creator":{"username":"someone","image":null},"tags":[],"modelVersions":[{"id":3220300,"index":0,"name":"v1.0","baseModel":"Krea 2","baseModelType":"Standard","publishedAt":"2026-08-11T21:26:30.000Z","flags":0,"availability":"Public","nsfwLevel":0,"description":null,"trainedWords":[],"vaeId":null,"paidAccess":null,"stats":{"downloadCount":null,"thumbsUpCount":null,"thumbsDownCount":null},"supportsGeneration":false,"files":[],"images":[]}]}
            ]}
            """;

        var page = JsonSerializer.Deserialize<CivitaiPagedResponse<CivitaiModel>>(raw);

        page!.Items.Should().HaveCount(2);

        var healthy = page.Items[0];
        healthy.Name.Should().Be("Pocahontas - Disney - Krea2 LORA");
        healthy.Stats!.DownloadCount.Should().Be(0);
        healthy.Stats.ThumbsUpCount.Should().Be(1);
        healthy.ModelVersions.Should().ContainSingle()
            .Which.Stats!.DownloadCount.Should().Be(2);

        var fresh = page.Items[1];
        fresh.Stats!.DownloadCount.Should().Be(0);
        fresh.Stats.ThumbsUpCount.Should().Be(0);
        fresh.ModelVersions.Should().ContainSingle()
            .Which.Stats!.DownloadCount.Should().Be(0);
    }

    [Fact]
    public void CivitaiModel_StillTreatsWholeNullStatsObjectAsNull()
    {
        // "stats": null already worked (the property is nullable) — keep it that way.
        var json = """{"id":1,"name":"x","stats":null}""";

        var model = JsonSerializer.Deserialize<CivitaiModel>(json);

        model!.Stats.Should().BeNull();
    }

    [Fact]
    public void SidecarModelData_ReadsNullStatsCountersAsZero()
    {
        // The sidecar/download DTO family parses the same API responses, so a fresh
        // model with null stats must not fail "Download LoRA" or sidecar reads either.
        var json = """
            {"id":7,"name":"sidecar","stats":{"downloadCount":null,"thumbsUpCount":null,
             "thumbsDownCount":null,"commentCount":null,"tippedAmountCount":null},
             "modelVersions":[{"id":8,"name":"v1","stats":{"downloadCount":null,
             "thumbsUpCount":null,"thumbsDownCount":null}}]}
            """;

        var data = JsonSerializer.Deserialize<ModelData>(json);

        data!.Stats.Should().NotBeNull();
        data.Stats!.DownloadCount.Should().Be(0);
        data.Stats.ThumbsUpCount.Should().Be(0);
        data.Stats.ThumbsDownCount.Should().Be(0);
        data.Stats.CommentCount.Should().Be(0);
        data.Stats.TippedAmountCount.Should().Be(0);
        data.ModelVersions.Should().ContainSingle()
            .Which.Stats!.DownloadCount.Should().Be(0);
    }
}
