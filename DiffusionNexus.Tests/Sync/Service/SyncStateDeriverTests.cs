using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Service.Services.Sync;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service;

/// <summary>
/// The derivation table from spec S3, one theory row per table line.
/// Every legacy model gets its state from data already on disk — never from the network.
/// <para>
/// The input is <see cref="SyncDerivationInput"/>, the projected record the backfill actually
/// derives from (R8/F3) — there is no entity-shaped overload any more. Each case is still written
/// in terms of the underlying rows (which base models the versions carry, which version holds an
/// image) and folded into the record the same way <c>SyncStateRepository.GetDerivationInputsAsync</c>
/// projects it; that the projection really answers this way is pinned by
/// <c>SyncStateRepositoryTests.GetDerivationInputs_ProjectsTheFactsTheDeriverAsksFor</c>.
/// </para>
/// </summary>
public sealed class SyncStateDeriverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Synced = new(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>Which timestamp a derived column is expected to carry.</summary>
    public enum Stamp
    {
        /// <summary>Column must be null.</summary>
        Null,

        /// <summary><c>LastSyncedAt ?? now</c>.</summary>
        Synced,

        /// <summary>Explicitly <c>now</c> (the model has no <c>LastSyncedAt</c>).</summary>
        Now,
    }

    private sealed record Case(
        int? CivitaiId,
        DateTimeOffset? LastSyncedAt,
        DataSource Source,
        string?[] BaseModelRaws,
        bool WithTag,
        int ImageOnVersionIndex,
        SyncOutcome ExpectedOutcome,
        Stamp ExpectedMetadata,
        Stamp ExpectedTags,
        Stamp ExpectedImages);

    private static readonly IReadOnlyDictionary<string, Case> Cases = new Dictionary<string, Case>
    {
        // Table line 1: CivitaiId != null => Matched.
        ["matched-with-tags-and-images"] = new(
            CivitaiId: 42, LastSyncedAt: Synced, Source: DataSource.CivitaiApi, BaseModelRaws: ["SDXL 1.0"],
            WithTag: true, ImageOnVersionIndex: 0,
            ExpectedOutcome: SyncOutcome.Matched,
            ExpectedMetadata: Stamp.Synced, ExpectedTags: Stamp.Synced, ExpectedImages: Stamp.Synced),

        // Table line 1, tagless + imageless: those columns stay null on purpose so the
        // "asked one final time, then stamped" path still runs for them.
        ["matched-without-tags-or-images"] = new(
            CivitaiId: 42, LastSyncedAt: Synced, Source: DataSource.CivitaiApi, BaseModelRaws: ["SDXL 1.0"],
            WithTag: false, ImageOnVersionIndex: -1,
            ExpectedOutcome: SyncOutcome.Matched,
            ExpectedMetadata: Stamp.Synced, ExpectedTags: Stamp.Null, ExpectedImages: Stamp.Null),

        // Table line 2: no Civitai id, synced, local file, real base model => Sidecar.
        // The stamp is `now`, not LastSyncedAt: the upgrade itself counts as the check, so the
        // whole legacy library does not fall due on the first run after it (R1, anti-herd).
        ["sidecar-local-file-with-real-base-model"] = new(
            CivitaiId: null, LastSyncedAt: Synced, Source: DataSource.LocalFile, BaseModelRaws: ["Illustrious"],
            WithTag: true, ImageOnVersionIndex: 0,
            ExpectedOutcome: SyncOutcome.Sidecar,
            ExpectedMetadata: Stamp.Now, ExpectedTags: Stamp.Null, ExpectedImages: Stamp.Null),

        // Table line 3 variants: synced, but nothing actually identified the model.
        ["not-identified-placeholder-base-model"] = new(
            CivitaiId: null, LastSyncedAt: Synced, Source: DataSource.LocalFile, BaseModelRaws: ["???"],
            WithTag: false, ImageOnVersionIndex: -1,
            ExpectedOutcome: SyncOutcome.NotIdentified,
            ExpectedMetadata: Stamp.Now, ExpectedTags: Stamp.Null, ExpectedImages: Stamp.Null),

        ["not-identified-blank-base-model"] = new(
            CivitaiId: null, LastSyncedAt: Synced, Source: DataSource.LocalFile, BaseModelRaws: ["   ", null],
            WithTag: false, ImageOnVersionIndex: -1,
            ExpectedOutcome: SyncOutcome.NotIdentified,
            ExpectedMetadata: Stamp.Now, ExpectedTags: Stamp.Null, ExpectedImages: Stamp.Null),

        ["not-identified-no-versions-at-all"] = new(
            CivitaiId: null, LastSyncedAt: Synced, Source: DataSource.LocalFile, BaseModelRaws: [],
            WithTag: false, ImageOnVersionIndex: -1,
            ExpectedOutcome: SyncOutcome.NotIdentified,
            ExpectedMetadata: Stamp.Now, ExpectedTags: Stamp.Null, ExpectedImages: Stamp.Null),

        ["not-identified-non-local-source"] = new(
            CivitaiId: null, LastSyncedAt: Synced, Source: DataSource.Manual, BaseModelRaws: ["Flux.1 D"],
            WithTag: false, ImageOnVersionIndex: -1,
            ExpectedOutcome: SyncOutcome.NotIdentified,
            ExpectedMetadata: Stamp.Now, ExpectedTags: Stamp.Null, ExpectedImages: Stamp.Null),

        // Table line 4: never synced, never matched => nothing was ever checked.
        ["none-never-synced"] = new(
            CivitaiId: null, LastSyncedAt: null, Source: DataSource.LocalFile, BaseModelRaws: ["Pony"],
            WithTag: true, ImageOnVersionIndex: 0,
            ExpectedOutcome: SyncOutcome.None,
            ExpectedMetadata: Stamp.Null, ExpectedTags: Stamp.Null, ExpectedImages: Stamp.Null),

        // Extra 1: matched but never stamped with LastSyncedAt => every timestamp is `now`.
        ["matched-without-last-synced-falls-back-to-now"] = new(
            CivitaiId: 7, LastSyncedAt: null, Source: DataSource.CivitaiApi, BaseModelRaws: ["SD 1.5"],
            WithTag: true, ImageOnVersionIndex: 0,
            ExpectedOutcome: SyncOutcome.Matched,
            ExpectedMetadata: Stamp.Now, ExpectedTags: Stamp.Now, ExpectedImages: Stamp.Now),

        // Extra 2: images living on a NON-first version still count.
        ["matched-images-on-second-version-count"] = new(
            CivitaiId: 7, LastSyncedAt: Synced, Source: DataSource.CivitaiApi, BaseModelRaws: ["SD 1.5", "SD 1.5"],
            WithTag: false, ImageOnVersionIndex: 1,
            ExpectedOutcome: SyncOutcome.Matched,
            ExpectedMetadata: Stamp.Synced, ExpectedTags: Stamp.Null, ExpectedImages: Stamp.Synced),
    };

    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Cases.Keys) data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void DeriveFollowsTheSpecTable(string caseName)
    {
        var testCase = Cases[caseName];
        var input = BuildInput(testCase);

        var state = SyncStateDeriver.Derive(input, Now);

        state.ModelId.Should().Be(input.ModelId);
        state.MetadataOutcome.Should().Be(testCase.ExpectedOutcome);
        state.MetadataCheckedAt.Should().Be(Expected(testCase, testCase.ExpectedMetadata));
        state.TagsCheckedAt.Should().Be(Expected(testCase, testCase.ExpectedTags));
        state.ImagesCheckedAt.Should().Be(Expected(testCase, testCase.ExpectedImages));

        // Constant for every derived row: nothing was attempted, nothing failed, no sidecar
        // signature was computed, the safetensors header was never read.
        state.MetadataAttempts.Should().Be(0);
        state.LastError.Should().BeNull();
        state.SidecarSignature.Should().BeNull();
        state.HeaderCheckedAt.Should().BeNull();
        state.UpdatedAt.Should().Be(Now);
    }

    /// <summary>
    /// The anti-herd guarantee (R1). A library synced years ago carries a <c>LastSyncedAt</c> far
    /// outside the 30-day retry window, so stamping the derived row with it would make every
    /// unidentified model due the instant the state table appears — the 545-item, 27-minute first
    /// run the live dry run measured. The upgrade counts as the check, so the stamp is <c>now</c>.
    /// </summary>
    [Fact]
    public void LegacyNotIdentifiedIsNotDueImmediatelyAfterUpgrade()
    {
        var input = BuildInput(Cases["not-identified-placeholder-base-model"]);

        var state = SyncStateDeriver.Derive(input, Now);

        state.MetadataOutcome.Should().Be(SyncOutcome.NotIdentified);
        state.MetadataCheckedAt.Should().Be(Now);
        SyncRetryPolicy.Default
            .IsIdentifyDue(state.MetadataOutcome, state.MetadataCheckedAt, 0, Now, false)
            .Should().BeFalse();

        // Still due once the retry window has actually elapsed — the check is deferred, not cancelled.
        SyncRetryPolicy.Default
            .IsIdentifyDue(state.MetadataOutcome, state.MetadataCheckedAt, 0, Now.Add(SyncRetryPolicy.Default.NotIdentifiedRetryAfter), false)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("???", true)]
    [InlineData("SDXL 1.0", false)]
    [InlineData("Unknown", false)]
    public void IsPlaceholderRecognisesBlanksAndQuestionMarks(string? raw, bool expected)
    {
        SyncStateDeriver.IsPlaceholder(raw).Should().Be(expected);
    }

    private static DateTimeOffset? Expected(Case testCase, Stamp stamp) => stamp switch
    {
        Stamp.Null => null,
        Stamp.Now => Now,
        Stamp.Synced => testCase.LastSyncedAt ?? Now,
        _ => throw new ArgumentOutOfRangeException(nameof(stamp)),
    };

    /// <summary>
    /// Folds a case's underlying rows into the record the repository projects — the same three
    /// questions, asked the same way: any tag at all, any version with any image, any version whose
    /// base model is not a placeholder.
    /// </summary>
    private static SyncDerivationInput BuildInput(Case testCase) => new(
        ModelId: 11,
        CivitaiId: testCase.CivitaiId,
        LastSyncedAt: testCase.LastSyncedAt,
        Source: testCase.Source,
        HasTags: testCase.WithTag,
        HasImages: testCase.ImageOnVersionIndex >= 0,
        HasRealBaseModel: testCase.BaseModelRaws.Any(raw => !SyncStateDeriver.IsPlaceholder(raw)));
}
