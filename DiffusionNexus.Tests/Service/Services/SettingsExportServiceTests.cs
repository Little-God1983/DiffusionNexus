using System.Text.Json;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// Pins the schema-compatibility contract of <see cref="SettingsExportService"/>:
/// a settings file written by any supported app version must still import, and a
/// file written by a <i>newer</i> version must import while silently dropping
/// fields this build does not know about.
/// </summary>
public class SettingsExportServiceTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("dn-settings-export-tests-");
    private readonly Mock<IAppSettingsService> _settingsService = new(MockBehavior.Strict);

    private string PathFor(string fileName) => Path.Combine(_tempDir.FullName, fileName);

    private SettingsExportService CreateSut() => new(_settingsService.Object);

    /// <summary>Stubs <c>GetSettingsAsync</c> so <c>ExportAsync</c> has something to write.</summary>
    private void GivenCurrentSettings(AppSettings settings) =>
        _settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

    /// <summary>Captures whatever <c>ImportAsync</c> persists.</summary>
    private Func<AppSettings?> GivenSaveIsCaptured()
    {
        AppSettings? captured = null;
        _settingsService
            .Setup(s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppSettings, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);
        return () => captured;
    }

    private async Task<string> WriteJsonAsync(string fileName, string json)
    {
        var path = PathFor(fileName);
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private static AppSettings FullyPopulatedSettings() => new()
    {
        Id = 1,
        EncryptedCivitaiApiKey = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA-civitai",
        EncryptedHuggingfaceApiKey = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA-hf",

        ShowNsfw = true,
        GenerateVideoThumbnails = false,
        ShowVideoPreview = true,
        UseForgeStylePrompts = false,
        MergeLoraSources = true,
        LoraUpdateCheckStalenessDays = 17,

        LoraSortSourcePath = @"D:\Sort\In",
        LoraSortTargetPath = @"D:\Sort\Out",
        DeleteEmptySourceFolders = true,

        DatasetStoragePath = @"D:\Datasets",
        BackupDatasetImagesEnabled = true,
        BackupDatabaseEnabled = false,
        AutoBackupIntervalDays = 6,
        AutoBackupIntervalHours = 13,
        AutoBackupLocation = @"D:\Backups",
        MaxBackups = 42,

        ComfyUiServerUrl = "http://192.168.0.42:9999/"
    };

    // ── Round trip ───────────────────────────────────────────────────────

    [Fact]
    public async Task WhenSettingsAreExportedAndReimportedThenEveryScalarValueSurvives()
    {
        var original = FullyPopulatedSettings();
        GivenCurrentSettings(original);
        var captured = GivenSaveIsCaptured();
        var sut = CreateSut();
        var path = PathFor("round-trip.json");

        await sut.ExportAsync(path);
        await sut.ImportAsync(path);

        var imported = captured();
        imported.Should().NotBeNull();
        imported!.ShowNsfw.Should().BeTrue();
        imported.GenerateVideoThumbnails.Should().BeFalse();
        imported.ShowVideoPreview.Should().BeTrue();
        imported.UseForgeStylePrompts.Should().BeFalse();
        imported.MergeLoraSources.Should().BeTrue();
        imported.LoraUpdateCheckStalenessDays.Should().Be(17);
        imported.LoraSortSourcePath.Should().Be(@"D:\Sort\In");
        imported.LoraSortTargetPath.Should().Be(@"D:\Sort\Out");
        imported.DeleteEmptySourceFolders.Should().BeTrue();
        imported.DatasetStoragePath.Should().Be(@"D:\Datasets");
        imported.BackupDatasetImagesEnabled.Should().BeTrue();
        imported.BackupDatabaseEnabled.Should().BeFalse();
        imported.AutoBackupIntervalDays.Should().Be(6);
        imported.AutoBackupIntervalHours.Should().Be(13);
        imported.AutoBackupLocation.Should().Be(@"D:\Backups");
        imported.MaxBackups.Should().Be(42);
        imported.ComfyUiServerUrl.Should().Be("http://192.168.0.42:9999/");
    }

    [Fact]
    public async Task WhenSettingsAreExportedAndReimportedThenEncryptedApiKeysSurviveVerbatim()
    {
        // The keys travel as opaque cipher text — the export layer must never
        // re-encode, trim or drop them, or the user silently loses their tokens.
        var original = FullyPopulatedSettings();
        GivenCurrentSettings(original);
        var captured = GivenSaveIsCaptured();
        var sut = CreateSut();
        var path = PathFor("keys.json");

        await sut.ExportAsync(path);
        await sut.ImportAsync(path);

        captured()!.EncryptedCivitaiApiKey.Should().Be(original.EncryptedCivitaiApiKey);
        captured()!.EncryptedHuggingfaceApiKey.Should().Be(original.EncryptedHuggingfaceApiKey);
    }

    [Fact]
    public async Task WhenApiKeysAreNotSetThenExportOmitsThemAndImportKeepsThemNull()
    {
        var original = FullyPopulatedSettings();
        original.EncryptedCivitaiApiKey = null;
        original.EncryptedHuggingfaceApiKey = null;
        GivenCurrentSettings(original);
        var captured = GivenSaveIsCaptured();
        var sut = CreateSut();
        var path = PathFor("no-keys.json");

        await sut.ExportAsync(path);

        // DefaultIgnoreCondition = WhenWritingNull: absent, not "null".
        var json = await File.ReadAllTextAsync(path);
        json.Should().NotContain("encryptedCivitaiApiKey");
        json.Should().NotContain("encryptedHuggingfaceApiKey");

        await sut.ImportAsync(path);
        captured()!.EncryptedCivitaiApiKey.Should().BeNull();
        captured()!.EncryptedHuggingfaceApiKey.Should().BeNull();
    }

    [Fact]
    public async Task WhenCollectionsAreExportedThenTheyRoundTripSortedAndRenumberedFromZero()
    {
        var original = FullyPopulatedSettings();
        // Deliberately unsorted and sparsely numbered.
        original.LoraSources = new List<LoraSource>
        {
            new() { FolderPath = @"C:\Loras\C", IsEnabled = false, Order = 50 },
            new() { FolderPath = @"C:\Loras\A", IsEnabled = true, Order = 5 },
            new() { FolderPath = @"C:\Loras\B", IsEnabled = true, Order = 20 }
        };
        original.ImageGalleries = new List<ImageGallery>
        {
            new() { FolderPath = @"C:\Gallery\Second", IsEnabled = true, Order = 9 },
            new() { FolderPath = @"C:\Gallery\First", IsEnabled = false, Order = 1 }
        };
        original.DatasetCategories = new List<DatasetCategory>
        {
            new() { Name = "Style", Description = "artistic", IsDefault = false, Order = 3 },
            new() { Name = "Character", Description = null, IsDefault = true, Order = 0 }
        };
        GivenCurrentSettings(original);
        var captured = GivenSaveIsCaptured();
        var sut = CreateSut();
        var path = PathFor("collections.json");

        await sut.ExportAsync(path);
        await sut.ImportAsync(path);

        var imported = captured()!;

        imported.LoraSources.Select(x => x.FolderPath)
            .Should().ContainInOrder(@"C:\Loras\A", @"C:\Loras\B", @"C:\Loras\C");
        imported.LoraSources.Select(x => x.Order).Should().ContainInOrder(0, 1, 2);
        imported.LoraSources.Single(x => x.FolderPath == @"C:\Loras\C").IsEnabled.Should().BeFalse();

        imported.ImageGalleries.Select(x => x.FolderPath)
            .Should().ContainInOrder(@"C:\Gallery\First", @"C:\Gallery\Second");
        imported.ImageGalleries.Select(x => x.Order).Should().ContainInOrder(0, 1);
        imported.ImageGalleries.Single(x => x.FolderPath.EndsWith("First")).IsEnabled.Should().BeFalse();

        imported.DatasetCategories.Select(x => x.Name).Should().ContainInOrder("Character", "Style");
        imported.DatasetCategories.Single(x => x.Name == "Character").IsDefault.Should().BeTrue();
        imported.DatasetCategories.Single(x => x.Name == "Character").Description.Should().BeNull();
        imported.DatasetCategories.Single(x => x.Name == "Style").Description.Should().Be("artistic");
    }

    [Fact]
    public async Task WhenImportingThenChildRowsAreReparentedToTheSingletonSettingsRow()
    {
        var original = FullyPopulatedSettings();
        original.LoraSources = new List<LoraSource> { new() { FolderPath = @"C:\L", Order = 0 } };
        original.ImageGalleries = new List<ImageGallery> { new() { FolderPath = @"C:\G", Order = 0 } };
        original.DatasetCategories = new List<DatasetCategory> { new() { Name = "Concept", Order = 0 } };
        GivenCurrentSettings(original);
        var captured = GivenSaveIsCaptured();
        var sut = CreateSut();
        var path = PathFor("reparent.json");

        await sut.ExportAsync(path);
        await sut.ImportAsync(path);

        var imported = captured()!;
        imported.Id.Should().Be(1, "AppSettings is a singleton row");
        imported.LoraSources.Should().OnlyContain(x => x.AppSettingsId == 1);
        imported.ImageGalleries.Should().OnlyContain(x => x.AppSettingsId == 1);
        imported.DatasetCategories.Should().OnlyContain(x => x.AppSettingsId == 1);
    }

    [Fact]
    public async Task WhenSettingsHaveNoCollectionsThenExportAndImportProduceEmptyCollections()
    {
        var original = FullyPopulatedSettings();
        GivenCurrentSettings(original);
        var captured = GivenSaveIsCaptured();
        var sut = CreateSut();
        var path = PathFor("empty-collections.json");

        await sut.ExportAsync(path);
        await sut.ImportAsync(path);

        var imported = captured()!;
        imported.LoraSources.Should().BeEmpty();
        imported.ImageGalleries.Should().BeEmpty();
        imported.DatasetCategories.Should().BeEmpty();
    }

    // ── Export file shape ────────────────────────────────────────────────

    [Fact]
    public async Task WhenExportingThenFileCarriesCurrentSchemaVersionAndCamelCaseNames()
    {
        GivenCurrentSettings(FullyPopulatedSettings());
        var sut = CreateSut();
        var path = PathFor("shape.json");

        await sut.ExportAsync(path);

        File.Exists(path).Should().BeTrue();
        var json = await File.ReadAllTextAsync(path);
        json.Should().Contain("\"schemaVersion\"").And.Contain("\"showNsfw\"");
        json.Should().NotContain("\"ShowNsfw\"", "the naming policy is camelCase");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("schemaVersion").GetInt32()
            .Should().Be(SettingsExportSchema.CurrentVersion);
    }

    [Fact]
    public async Task WhenExportingThenTheLegacyV1BackupFlagIsNeverWritten()
    {
        // LegacyAutoBackupEnabled is an import-only shim; writing it would make
        // new files re-trigger the v1 fallback on the next import.
        GivenCurrentSettings(FullyPopulatedSettings());
        var sut = CreateSut();
        var path = PathFor("no-legacy.json");

        await sut.ExportAsync(path);

        (await File.ReadAllTextAsync(path)).Should().NotContain("autoBackupEnabled");
    }

    [Fact]
    public async Task WhenExportingThenExportedAtIsStampedInUtc()
    {
        GivenCurrentSettings(FullyPopulatedSettings());
        var sut = CreateSut();
        var path = PathFor("stamp.json");
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        await sut.ExportAsync(path);
        var read = await sut.ReadAsync(path);

        read.ExportedAt.Should().BeOnOrAfter(before);
        read.ExportedAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(5));
        read.ExportedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task WhenExportingOverAnExistingFileThenItIsReplacedNotAppended()
    {
        GivenCurrentSettings(FullyPopulatedSettings());
        var sut = CreateSut();
        var path = await WriteJsonAsync("overwrite.json", new string('x', 20_000));

        await sut.ExportAsync(path);

        var json = await File.ReadAllTextAsync(path);
        json.Should().NotContain("xxxx");
        var parse = () => JsonDocument.Parse(json);
        parse.Should().NotThrow();
    }

    [Fact]
    public async Task WhenExportTargetFolderDoesNotExistThenAnIoExceptionSurfaces()
    {
        GivenCurrentSettings(FullyPopulatedSettings());
        var sut = CreateSut();
        var path = Path.Combine(_tempDir.FullName, "no-such-folder", "export.json");

        var act = async () => await sut.ExportAsync(path);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    // ── Forward compatibility ────────────────────────────────────────────

    [Fact]
    public async Task WhenFileContainsUnknownPropertiesThenTheyAreSkippedAndKnownOnesStillLoad()
    {
        var path = await WriteJsonAsync("forward.json", """
        {
          "schemaVersion": 2,
          "appVersion": "99.0.0-from-the-future",
          "showNsfw": true,
          "maxBackups": 7,
          "comfyUiServerUrl": "http://future:8188/",
          "quantumSyncEnabled": true,
          "futureNestedObject": { "a": 1, "b": [ 1, 2, { "c": null } ] },
          "loraSources": [
            { "folderPath": "C:\\Future", "isEnabled": true, "order": 0, "colorTag": "#ff00ff" }
          ]
        }
        """);
        var sut = CreateSut();

        var read = await sut.ReadAsync(path);

        read.ShowNsfw.Should().BeTrue();
        read.MaxBackups.Should().Be(7);
        read.ComfyUiServerUrl.Should().Be("http://future:8188/");
        read.AppVersion.Should().Be("99.0.0-from-the-future");
        read.LoraSources.Should().ContainSingle()
            .Which.FolderPath.Should().Be(@"C:\Future");
    }

    [Fact]
    public async Task WhenSchemaVersionIsNewerThanThisBuildThenTheFileIsStillAccepted()
    {
        // There is no upper bound check — only a floor. A file from a future
        // release must degrade gracefully rather than be rejected outright.
        var path = await WriteJsonAsync("future-version.json", """
        { "schemaVersion": 9999, "showNsfw": true }
        """);
        var sut = CreateSut();

        var read = await sut.ReadAsync(path);

        read.SchemaVersion.Should().Be(9999);
        read.ShowNsfw.Should().BeTrue();
    }

    [Fact]
    public async Task WhenFileHasUnknownPropertiesThenImportStillPersistsSettings()
    {
        var captured = GivenSaveIsCaptured();
        var path = await WriteJsonAsync("forward-import.json", """
        {
          "schemaVersion": 3,
          "showNsfw": true,
          "somethingWeHaveNeverSeen": [ "a", "b" ]
        }
        """);
        var sut = CreateSut();

        await sut.ImportAsync(path);

        captured()!.ShowNsfw.Should().BeTrue();
    }

    // ── Backward compatibility ───────────────────────────────────────────

    [Fact]
    public async Task WhenPropertiesAreMissingThenDeclaredDefaultsAreUsedInsteadOfThrowing()
    {
        var path = await WriteJsonAsync("minimal.json", """{ "schemaVersion": 1 }""");
        var sut = CreateSut();

        var read = await sut.ReadAsync(path);

        read.GenerateVideoThumbnails.Should().BeTrue();
        read.UseForgeStylePrompts.Should().BeTrue();
        read.LoraUpdateCheckStalenessDays.Should().Be(3);
        read.BackupDatabaseEnabled.Should().BeTrue();
        read.AutoBackupIntervalDays.Should().Be(1);
        read.MaxBackups.Should().Be(10);
        read.ComfyUiServerUrl.Should().Be("http://127.0.0.1:8188/");
        read.ShowNsfw.Should().BeFalse();
        read.LoraSources.Should().BeEmpty();
        read.ImageGalleries.Should().BeEmpty();
        read.DatasetCategories.Should().BeEmpty();
        read.EncryptedCivitaiApiKey.Should().BeNull();
    }

    [Fact]
    public async Task WhenAnEmptyJsonObjectIsImportedThenDefaultsArePersisted()
    {
        // No schemaVersion at all: the DTO default (CurrentVersion) applies,
        // so the file must not be rejected by the minimum-version guard.
        var captured = GivenSaveIsCaptured();
        var path = await WriteJsonAsync("empty-object.json", "{}");
        var sut = CreateSut();

        await sut.ImportAsync(path);

        var imported = captured()!;
        imported.MaxBackups.Should().Be(10);
        imported.ComfyUiServerUrl.Should().Be("http://127.0.0.1:8188/");
        imported.GenerateVideoThumbnails.Should().BeTrue();
    }

    [Fact]
    public async Task WhenImportingAV1FileThenTheLegacyAutoBackupFlagBecomesTheDatasetImageFlag()
    {
        var captured = GivenSaveIsCaptured();
        var path = await WriteJsonAsync("v1.json", """
        { "schemaVersion": 1, "autoBackupEnabled": true, "autoBackupLocation": "D:\\Old" }
        """);
        var sut = CreateSut();

        await sut.ImportAsync(path);

        var imported = captured()!;
        imported.BackupDatasetImagesEnabled.Should().BeTrue("the v1 flag maps onto the v2 dataset-image flag");
        imported.AutoBackupLocation.Should().Be(@"D:\Old");
        imported.BackupDatabaseEnabled.Should().BeTrue("v1 files predate the flag, so its default applies");
    }

    [Fact]
    public async Task WhenAV1FileHasAutoBackupDisabledThenDatasetImageBackupStaysOff()
    {
        var captured = GivenSaveIsCaptured();
        var path = await WriteJsonAsync("v1-off.json", """
        { "schemaVersion": 1, "autoBackupEnabled": false }
        """);
        var sut = CreateSut();

        await sut.ImportAsync(path);

        captured()!.BackupDatasetImagesEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task WhenBothTheLegacyAndModernFlagsArePresentThenTheExplicitModernFalseWins()
    {
        // The v2 field, when explicitly present, is authoritative — even when it
        // says "false" and the stale v1 key says "true". The legacy key must only
        // fill in when the modern one is genuinely absent from the document.
        var captured = GivenSaveIsCaptured();
        var path = await WriteJsonAsync("both-flags.json", """
        { "schemaVersion": 2, "backupDatasetImagesEnabled": false, "autoBackupEnabled": true }
        """);
        var sut = CreateSut();

        await sut.ImportAsync(path);

        captured()!.BackupDatasetImagesEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheModernFlagIsTrueAndTheLegacyFlagIsTrueThenItStaysTrue()
    {
        var captured = GivenSaveIsCaptured();
        var path = await WriteJsonAsync("both-flags-true.json", """
        { "schemaVersion": 2, "backupDatasetImagesEnabled": true, "autoBackupEnabled": true }
        """);
        var sut = CreateSut();

        await sut.ImportAsync(path);

        captured()!.BackupDatasetImagesEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task WhenAV2FileOmitsTheLegacyFlagThenTheModernFlagIsHonoredAsIs()
    {
        var captured = GivenSaveIsCaptured();
        var path = await WriteJsonAsync("v2-only.json", """
        { "schemaVersion": 2, "backupDatasetImagesEnabled": false, "backupDatabaseEnabled": false }
        """);
        var sut = CreateSut();

        await sut.ImportAsync(path);

        captured()!.BackupDatasetImagesEnabled.Should().BeFalse();
        captured()!.BackupDatabaseEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheModernFlagIsTrueAndTheLegacyFlagIsAbsentThenTheModernValueIsHonored()
    {
        var captured = GivenSaveIsCaptured();
        var path = await WriteJsonAsync("v2-only-true.json", """
        { "schemaVersion": 2, "backupDatasetImagesEnabled": true }
        """);
        var sut = CreateSut();

        await sut.ImportAsync(path);

        captured()!.BackupDatasetImagesEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task WhenSettingsWithBackupDatasetImagesDisabledAreExportedAndReimportedThenTheFlagStaysFalse()
    {
        // Regression for #431: a settings row where the modern flag is explicitly
        // false must round-trip through a real export/import unchanged — the
        // legacy key must never resurrect it back to true on re-import.
        var original = FullyPopulatedSettings();
        original.BackupDatasetImagesEnabled = false;
        GivenCurrentSettings(original);
        var captured = GivenSaveIsCaptured();
        var sut = CreateSut();
        var path = PathFor("backup-flag-round-trip.json");

        await sut.ExportAsync(path);
        await sut.ImportAsync(path);

        captured()!.BackupDatasetImagesEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task WhenBothTheModernAndLegacyFlagsAreAbsentThenTheDefaultIsFalse()
    {
        var captured = GivenSaveIsCaptured();
        var path = await WriteJsonAsync("both-absent.json", """{ "schemaVersion": 2 }""");
        var sut = CreateSut();

        await sut.ImportAsync(path);

        captured()!.BackupDatasetImagesEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheModernFlagKeyIsGenuinelyAbsentFromTheDocumentThenDeserializationYieldsNullNotFalse()
    {
        // The whole fix hinges on being able to tell "absent" apart from "false" at
        // the DTO level. If this ever degraded back to a non-nullable bool, this
        // assertion — not just the merged-import behavior — would catch it.
        var path = await WriteJsonAsync("modern-key-absent.json", """
        { "schemaVersion": 1, "autoBackupEnabled": true }
        """);
        var sut = CreateSut();

        var read = await sut.ReadAsync(path);

        read.BackupDatasetImagesEnabled.Should().BeNull();
        read.LegacyAutoBackupEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task WhenSchemaVersionIsBelowTheMinimumThenImportIsRejectedWithAnExplanation()
    {
        var path = await WriteJsonAsync("too-old.json", """{ "schemaVersion": 0 }""");
        var sut = CreateSut();

        var act = async () => await sut.ReadAsync(path);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(SettingsExportSchema.MinSupportedVersion.ToString());
    }

    [Fact]
    public async Task WhenSchemaVersionIsBelowTheMinimumThenNothingIsPersisted()
    {
        // Strict mock: an unexpected SaveSettingsAsync call would itself fail the test.
        var path = await WriteJsonAsync("too-old-import.json", """{ "schemaVersion": -1 }""");
        var sut = CreateSut();

        var act = async () => await sut.ImportAsync(path);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _settingsService.Verify(
            s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenSchemaVersionEqualsTheMinimumThenTheFileIsAccepted()
    {
        var path = await WriteJsonAsync("min-version.json",
            $$"""{ "schemaVersion": {{SettingsExportSchema.MinSupportedVersion}} }""");
        var sut = CreateSut();

        var act = async () => await sut.ReadAsync(path);

        await act.Should().NotThrowAsync();
    }

    // ── Malformed input ──────────────────────────────────────────────────

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public async Task WhenTheFileIsNotValidSettingsJsonThenAJsonExceptionSurfaces(string content)
    {
        var path = await WriteJsonAsync("malformed.json", content);
        var sut = CreateSut();

        var act = async () => await sut.ReadAsync(path);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task WhenTheFileContainsJsonNullThenAnInvalidOperationExceptionExplainsIt()
    {
        // The only path that hits the explicit null guard rather than the parser.
        var path = await WriteJsonAsync("null.json", "null");
        var sut = CreateSut();

        var act = async () => await sut.ReadAsync(path);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("invalid JSON");
    }

    [Fact]
    public async Task WhenMalformedJsonIsImportedThenNoSettingsArePersisted()
    {
        var path = await WriteJsonAsync("malformed-import.json", "{ \"schemaVersion\": ");
        var sut = CreateSut();

        var act = async () => await sut.ImportAsync(path);

        await act.Should().ThrowAsync<JsonException>();
        _settingsService.Verify(
            s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenTheFileDoesNotExistThenAFileNotFoundExceptionSurfaces()
    {
        var sut = CreateSut();

        var act = async () => await sut.ReadAsync(PathFor("absent.json"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ── Wiring ───────────────────────────────────────────────────────────

    [Fact]
    public void WhenConstructedWithoutASettingsServiceThenAnArgumentNullExceptionIsThrown()
    {
        var act = () => new SettingsExportService(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WhenExportingThenTheCallersCancellationTokenReachesTheSettingsService()
    {
        GivenCurrentSettings(FullyPopulatedSettings());
        using var cts = new CancellationTokenSource();
        var sut = CreateSut();

        await sut.ExportAsync(PathFor("token.json"), cts.Token);

        _settingsService.Verify(s => s.GetSettingsAsync(cts.Token), Times.Once);
    }

    [Fact]
    public async Task WhenImportingThenTheCallersCancellationTokenReachesTheSettingsService()
    {
        GivenCurrentSettings(FullyPopulatedSettings());
        GivenSaveIsCaptured();
        using var cts = new CancellationTokenSource();
        var sut = CreateSut();
        var path = PathFor("token-import.json");
        await sut.ExportAsync(path);

        await sut.ImportAsync(path, cts.Token);

        _settingsService.Verify(
            s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task WhenTheTokenIsAlreadyCancelledThenImportDoesNotPersistAnything()
    {
        var path = await WriteJsonAsync("cancelled.json", """{ "schemaVersion": 2 }""");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = CreateSut();

        var act = async () => await sut.ImportAsync(path, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _settingsService.Verify(
            s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose()
    {
        try
        {
            _tempDir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort — never fail a test run on temp-folder cleanup.
        }
        GC.SuppressFinalize(this);
    }
}
