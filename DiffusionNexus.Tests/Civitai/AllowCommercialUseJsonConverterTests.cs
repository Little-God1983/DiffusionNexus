using System.Text.Json;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Service.Classes.CivitaiModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Civitai changed <c>allowCommercialUse</c> from a string (historically set-notation,
/// e.g. "{Image,Rent,Sell}") to a JSON array of strings. Both typed DTOs declared it
/// <c>string?</c>, so any model fetch died with "The JSON value could not be converted
/// to System.String" — the Installed tab's "Download LoRA" button was broken for every
/// current model (user-reported with model 2833924, "Mihaila Krea"). The converter must
/// accept the array form, the legacy string form, null, and future shapes without
/// failing the whole payload.
/// </summary>
public class AllowCommercialUseJsonConverterTests
{
    /// <summary>Condensed real payload from the failing request (models/2833924).</summary>
    private const string ArrayFormJson =
        """
        {"id":2833924,"name":"Mihaila Krea","allowNoCredit":true,"allowCommercialUse":[],
         "allowDerivatives":true,"allowDifferentLicense":false,"type":"LORA","nsfw":false,
         "baseModels":["Krea 2"],
         "modelVersions":[{"id":3198110,"name":"V1","baseModel":"Krea 2"}]}
        """;

    [Fact]
    public void CivitaiModel_ToleratesEmptyArrayForm()
    {
        var model = JsonSerializer.Deserialize<CivitaiModel>(ArrayFormJson);

        model.Should().NotBeNull();
        model!.Id.Should().Be(2833924);
        model.AllowCommercialUse.Should().BeNull("an empty permissions array means none");
        model.ModelVersions.Should().ContainSingle();
    }

    [Fact]
    public void CivitaiModel_JoinsPopulatedArrayForm()
    {
        var json = """{"id":1,"name":"x","allowCommercialUse":["Image","Rent","Sell"]}""";

        var model = JsonSerializer.Deserialize<CivitaiModel>(json);

        model!.AllowCommercialUse.Should().Be("Image,Rent,Sell");
    }

    [Fact]
    public void CivitaiModel_KeepsLegacyStringForm()
    {
        var json = """{"id":1,"name":"x","allowCommercialUse":"{Image,RentCivit,Rent,Sell}"}""";

        var model = JsonSerializer.Deserialize<CivitaiModel>(json);

        model!.AllowCommercialUse.Should().Be("{Image,RentCivit,Rent,Sell}");
    }

    [Fact]
    public void CivitaiModel_ToleratesNull()
    {
        var json = """{"id":1,"name":"x","allowCommercialUse":null}""";

        var model = JsonSerializer.Deserialize<CivitaiModel>(json);

        model!.AllowCommercialUse.Should().BeNull();
    }

    [Fact]
    public void CivitaiModel_DeserializesTheCompleteRealFailingPayload()
    {
        // Verbatim response for models/2833924 from the user's log — the original
        // failure aborted at byte 135, so nothing past allowCommercialUse was ever
        // parsed. This proves the whole payload now round-trips.
        const string raw =
            """
            {"id":2833924,"name":"Mihaila Krea","description":"<p>OC lora, all training images sfw</p>","allowNoCredit":true,"allowCommercialUse":[],"allowDerivatives":true,"allowDifferentLicense":false,"type":"LORA","minor":false,"sfwOnly":false,"poi":false,"nsfw":false,"nsfwLevel":4,"availability":"Public","userId":3205088,"baseModels":["Krea 2"],"cosmetic":null,"supportsGeneration":false,"stats":{"downloadCount":16,"thumbsUpCount":1,"thumbsDownCount":0,"commentCount":0,"tippedAmountCount":0},"creator":{"username":"kestinek","image":"https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA/c0d9a114-91fc-4e13-9968-00e2217992e9/width=96/kestinek.jpeg"},"tags":["character","woman","star wars","warcraft","wizard","sith"],"modelVersions":[{"id":3198110,"index":0,"name":"V1","baseModel":"Krea 2","baseModelType":"Standard","createdAt":"2026-08-04T16:51:49.483Z","publishedAt":"2026-08-09T15:12:32.918Z","status":"Published","flags":0,"availability":"Public","nsfwLevel":0,"covered":false,"stats":{"downloadCount":16,"thumbsUpCount":1,"thumbsDownCount":0},"files":[{"id":3094942,"sizeKB":223231.3515625,"name":"Mihaila Krea V1.safetensors","overrideName":"Mihaila Krea V1.safetensors","type":"Model","pickleScanResult":"Success","pickleScanMessage":null,"virusScanResult":"Success","virusScanMessage":null,"scannedAt":"2026-08-09T15:02:50.938Z","metadata":{"format":"SafeTensor"},"hashes":{"AutoV1":"7FF63543","AutoV2":"5B4CD3D7DB","SHA256":"5B4CD3D7DB3E13E2E8BD8418921EFD8E11C1F5559B43DC0BE78522FEE3F5FC5B","CRC32":"AE78958B","BLAKE3":"1F6CA6FFF94D95F871E8EA7FBD7AB2237FB4F750B77D12E1D3A179987A51D5B8","AutoV3":"C26973DE6B79"},"downloadUrl":"https://civitai.com/api/download/models/3198110","primary":true}],"images":[{"url":"https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA/15f6397f-5063-4a3f-9c47-3f205c4b41b0/original=true/139221483.jpeg","nsfwLevel":4,"width":768,"height":1152,"hash":"U2BW6.ov03_30exH=Cxt00xt~p0M8^NftAn2","type":"image","minor":false,"poi":false,"hasMeta":true,"hasPositivePrompt":true,"onSite":false,"remixOfId":null}],"downloadUrl":"https://civitai.com/api/download/models/3198110"}]}
            """;

        var model = JsonSerializer.Deserialize<CivitaiModel>(raw);

        model.Should().NotBeNull();
        model!.Name.Should().Be("Mihaila Krea");
        model.AllowCommercialUse.Should().BeNull();
        var version = model.ModelVersions.Should().ContainSingle().Subject;
        version.Id.Should().Be(3198110);
        version.BaseModel.Should().Be("Krea 2");
        version.Files.Should().ContainSingle()
            .Which.Name.Should().Be("Mihaila Krea V1.safetensors");
    }

    [Fact]
    public void SidecarModelData_ToleratesArrayForm()
    {
        // .civitai.info sidecars are written from the same API responses, so freshly
        // written sidecars carry the array form too.
        var json = """{"id":7,"name":"sidecar","allowCommercialUse":["Image"]}""";

        var data = JsonSerializer.Deserialize<ModelData>(json);

        data!.AllowCommercialUse.Should().Be("Image");
    }
}
