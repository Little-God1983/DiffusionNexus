using System.Globalization;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="FileConflictDialogViewModel"/> and its item types.
/// <para>
/// All instances are created with <c>nonConflictingFilePaths: null</c> so the
/// constructor performs no file IO at all.
/// </para>
/// </summary>
public class FileConflictDialogViewModelTests
{
    private static FileConflictItem Item(string conflictingName,
        FileConflictResolution resolution = FileConflictResolution.Rename)
        => new()
        {
            ConflictingName = conflictingName,
            ExistingFilePath = $@"C:\dataset\{conflictingName}",
            NewFilePath = $@"C:\incoming\{conflictingName}",
            Resolution = resolution
        };

    private static FileConflictDialogViewModel CreateVm(params FileConflictItem[] items)
        => new(items, null);

    /// <summary>
    /// Runs <paramref name="action"/> with the invariant culture so number
    /// formatting in <c>FormatFileSize</c> is deterministic on machines with a
    /// comma decimal separator (e.g. de-DE).
    /// </summary>
    private static void WithInvariantCulture(Action action)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    #region Construction and counts

    [Fact]
    public void WhenConstructedWithNullNonConflictingPathsThenNoNonConflictingFilesAreAdded()
    {
        var vm = CreateVm(Item("a.png"));

        vm.NonConflictingFiles.Should().BeEmpty();
        vm.NonConflictingCount.Should().Be(0);
        vm.HasNonConflictingFiles.Should().BeFalse();
    }

    [Fact]
    public void WhenConstructedThenCountsReflectInitialResolutions()
    {
        var vm = CreateVm(
            Item("a.png", FileConflictResolution.Override),
            Item("b.png", FileConflictResolution.Rename),
            Item("c.png", FileConflictResolution.Ignore),
            Item("d.png", FileConflictResolution.Ignore));

        vm.OverrideCount.Should().Be(1);
        vm.RenameCount.Should().Be(1);
        vm.IgnoreCount.Should().Be(2);
        vm.ConflictCount.Should().Be(4);
        vm.TotalFileCount.Should().Be(4);
        vm.HasConflicts.Should().BeTrue();
    }

    [Fact]
    public void WhenConstructedWithNoConflictsThenAllCountsAreZero()
    {
        var vm = CreateVm();

        vm.ConflictCount.Should().Be(0);
        vm.OverrideCount.Should().Be(0);
        vm.RenameCount.Should().Be(0);
        vm.IgnoreCount.Should().Be(0);
        vm.HasConflicts.Should().BeFalse();
        vm.SummaryText.Should().Be("No actions selected");
    }

    [Fact]
    public void WhenDefaultItemIsUsedThenResolutionDefaultsToRename()
    {
        var item = new FileConflictItem { ConflictingName = "a.png" };

        item.Resolution.Should().Be(FileConflictResolution.Rename);
        item.IsRename.Should().BeTrue();
        item.IsOverride.Should().BeFalse();
        item.IsIgnore.Should().BeFalse();
    }

    #endregion

    #region UpdateCounts

    [Fact]
    public void WhenResolutionChangesThenCountsAreRecalculated()
    {
        var a = Item("a.png");
        var b = Item("b.png");
        var vm = CreateVm(a, b);

        vm.RenameCount.Should().Be(2);

        a.Resolution = FileConflictResolution.Override;

        vm.OverrideCount.Should().Be(1);
        vm.RenameCount.Should().Be(1);
        vm.IgnoreCount.Should().Be(0);
    }

    [Fact]
    public void WhenResolutionSetToSameValueThenNoNotificationAndCountsUnchanged()
    {
        var a = Item("a.png", FileConflictResolution.Rename);
        var vm = CreateVm(a);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        a.Resolution = FileConflictResolution.Rename;

        raised.Should().BeEmpty();
        vm.RenameCount.Should().Be(1);
    }

    [Fact]
    public void WhenCountsChangeThenSummaryAndAggregateNotificationsAreRaised()
    {
        var a = Item("a.png");
        var vm = CreateVm(a);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        a.Resolution = FileConflictResolution.Ignore;

        raised.Should().Contain(nameof(FileConflictDialogViewModel.SummaryText));
        raised.Should().Contain(nameof(FileConflictDialogViewModel.TotalFileCount));
        raised.Should().Contain(nameof(FileConflictDialogViewModel.HasNonConflictingFiles));
        raised.Should().Contain(nameof(FileConflictDialogViewModel.HasConflicts));
        raised.Should().Contain(nameof(FileConflictDialogViewModel.IgnoreCount));
        raised.Should().Contain(nameof(FileConflictDialogViewModel.RenameCount));
    }

    [Fact]
    public void WhenManyItemsShareOneBaseNameThenEachIsCountedIndividually()
    {
        // Cascading pairs must not collapse into a single counted entry.
        var vm = CreateVm(Item("x.png"), Item("x.txt"), Item("x.json"));

        vm.Conflicts[0].Resolution = FileConflictResolution.Override;

        vm.OverrideCount.Should().Be(3);
        vm.RenameCount.Should().Be(0);
        vm.ConflictCount.Should().Be(3);
    }

    #endregion

    #region SyncResolutionWithPairs

    [Fact]
    public void WhenImageResolutionChangesThenPairedCaptionFollows()
    {
        var image = Item("photo.png");
        var caption = Item("photo.txt");
        var vm = CreateVm(image, caption);

        image.Resolution = FileConflictResolution.Override;

        caption.Resolution.Should().Be(FileConflictResolution.Override);
        vm.OverrideCount.Should().Be(2);
        vm.RenameCount.Should().Be(0);
    }

    [Fact]
    public void WhenCaptionResolutionChangesThenPairedImageFollows()
    {
        var image = Item("photo.png");
        var caption = Item("photo.txt");
        CreateVm(image, caption);

        caption.Resolution = FileConflictResolution.Ignore;

        image.Resolution.Should().Be(FileConflictResolution.Ignore);
    }

    [Fact]
    public void WhenBaseNamesDifferOnlyByCaseThenResolutionStillCascades()
    {
        var image = Item("Photo.PNG");
        var caption = Item("photo.txt");
        var vm = CreateVm(image, caption);

        image.Resolution = FileConflictResolution.Ignore;

        caption.Resolution.Should().Be(FileConflictResolution.Ignore);
        vm.IgnoreCount.Should().Be(2);
    }

    [Fact]
    public void WhenBaseNamesDifferThenResolutionDoesNotCascade()
    {
        var a = Item("photo.png");
        var b = Item("other.txt");
        var vm = CreateVm(a, b);

        a.Resolution = FileConflictResolution.Override;

        b.Resolution.Should().Be(FileConflictResolution.Rename);
        vm.OverrideCount.Should().Be(1);
        vm.RenameCount.Should().Be(1);
    }

    [Fact]
    public void WhenThreeFilesShareABaseNameThenAllOfThemCascade()
    {
        var image = Item("shot.png");
        var caption = Item("shot.txt");
        var tags = Item("shot.tags");
        var unrelated = Item("shot2.png");
        var vm = CreateVm(image, caption, tags, unrelated);

        image.Resolution = FileConflictResolution.Override;

        caption.Resolution.Should().Be(FileConflictResolution.Override);
        tags.Resolution.Should().Be(FileConflictResolution.Override);
        unrelated.Resolution.Should().Be(FileConflictResolution.Rename);
        vm.OverrideCount.Should().Be(3);
    }

    [Fact]
    public void WhenPairsCascadeThenSyncTerminatesWithoutInfiniteRecursion()
    {
        // Two mutually-paired items: A syncs B, B's change event syncs A back.
        // The "only assign when different" guard is what stops the recursion.
        var a = Item("dup.png");
        var b = Item("dup.txt");
        var vm = CreateVm(a, b);

        var act = () => a.Resolution = FileConflictResolution.Ignore;

        act.Should().NotThrow();
        a.Resolution.Should().Be(FileConflictResolution.Ignore);
        b.Resolution.Should().Be(FileConflictResolution.Ignore);
        vm.IgnoreCount.Should().Be(2);
    }

    [Fact]
    public void WhenAPairAlreadyMatchesThenNoRedundantAssignmentOccurs()
    {
        var a = Item("dup.png", FileConflictResolution.Ignore);
        var b = Item("dup.txt", FileConflictResolution.Ignore);
        CreateVm(a, b);

        var bChanges = 0;
        b.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileConflictItem.Resolution)) bChanges++;
        };

        a.Resolution = FileConflictResolution.Override;

        // b changed exactly once (cascade), not repeatedly via ping-pong.
        bChanges.Should().Be(1);
        b.Resolution.Should().Be(FileConflictResolution.Override);
    }

    [Fact]
    public void WhenFileNameHasMultipleDotsThenOnlyTheLastExtensionIsStripped()
    {
        // Path.GetFileNameWithoutExtension("photo.v2.png") == "photo.v2"
        var a = Item("photo.v2.png");
        var b = Item("photo.v2.txt");
        var c = Item("photo.png");
        CreateVm(a, b, c);

        a.Resolution = FileConflictResolution.Override;

        b.Resolution.Should().Be(FileConflictResolution.Override);
        c.Resolution.Should().Be(FileConflictResolution.Rename);
    }

    [Fact]
    public void WhenConflictingNameHasNoExtensionThenItPairsOnTheWholeName()
    {
        var a = Item("README");
        var b = Item("README.txt");
        CreateVm(a, b);

        a.Resolution = FileConflictResolution.Ignore;

        b.Resolution.Should().Be(FileConflictResolution.Ignore);
    }

    #endregion

    #region SetAll commands

    [Fact]
    public void WhenSetAllOverrideThenEveryConflictIsOverride()
    {
        var a = Item("a.png", FileConflictResolution.Rename);
        var b = Item("b.txt", FileConflictResolution.Ignore);
        var c = Item("c.png", FileConflictResolution.Override);
        var vm = CreateVm(a, b, c);

        vm.SetAllOverrideCommand.Execute(null);

        vm.Conflicts.Should().OnlyContain(x => x.Resolution == FileConflictResolution.Override);
        vm.OverrideCount.Should().Be(3);
        vm.RenameCount.Should().Be(0);
        vm.IgnoreCount.Should().Be(0);
    }

    [Fact]
    public void WhenSetAllRenameThenEveryConflictIsRename()
    {
        var a = Item("a.png", FileConflictResolution.Override);
        var b = Item("b.txt", FileConflictResolution.Ignore);
        var vm = CreateVm(a, b);

        vm.SetAllRenameCommand.Execute(null);

        vm.Conflicts.Should().OnlyContain(x => x.Resolution == FileConflictResolution.Rename);
        vm.RenameCount.Should().Be(2);
        vm.OverrideCount.Should().Be(0);
        vm.IgnoreCount.Should().Be(0);
    }

    [Fact]
    public void WhenSetAllIgnoreThenEveryConflictIsIgnore()
    {
        var a = Item("a.png", FileConflictResolution.Override);
        var b = Item("b.txt", FileConflictResolution.Rename);
        var vm = CreateVm(a, b);

        vm.SetAllIgnoreCommand.Execute(null);

        vm.Conflicts.Should().OnlyContain(x => x.Resolution == FileConflictResolution.Ignore);
        vm.IgnoreCount.Should().Be(2);
    }

    [Fact]
    public void WhenSetAllRunsOverPairedItemsThenItDoesNotThrowFromNestedSync()
    {
        // SetAll* iterates Conflicts while the resolution-changed handler
        // iterates Conflicts again (nested enumeration).
        var vm = CreateVm(
            Item("p1.png"), Item("p1.txt"),
            Item("p2.png"), Item("p2.txt"));

        var act = () => vm.SetAllIgnoreCommand.Execute(null);

        act.Should().NotThrow();
        vm.IgnoreCount.Should().Be(4);
    }

    [Fact]
    public void WhenNoConflictsThenSetAllCommandsAreNoOps()
    {
        var vm = CreateVm();

        var act = () =>
        {
            vm.SetAllOverrideCommand.Execute(null);
            vm.SetAllRenameCommand.Execute(null);
            vm.SetAllIgnoreCommand.Execute(null);
        };

        act.Should().NotThrow();
        vm.OverrideCount.Should().Be(0);
    }

    #endregion

    #region Header and summary text

    [Fact]
    public void WhenOneConflictThenHeaderIsSingular()
    {
        CreateVm(Item("a.png")).HeaderText.Should().Be("1 file already exists in the dataset");
    }

    [Fact]
    public void WhenManyConflictsThenHeaderIsPlural()
    {
        CreateVm(Item("a.png"), Item("b.png")).HeaderText
            .Should().Be("2 files already exist in the dataset");
    }

    [Fact]
    public void WhenNoConflictsThenHeaderDescribesNonConflictingFiles()
    {
        CreateVm().HeaderText.Should().Be("0 files ready to add");
    }

    [Fact]
    public void WhenResolutionsAreMixedThenSummaryListsEachNonZeroBucket()
    {
        var vm = CreateVm(
            Item("a.png", FileConflictResolution.Override),
            Item("b.png", FileConflictResolution.Rename),
            Item("c.png", FileConflictResolution.Ignore));

        // Ignored files are excluded from the leading "images" total.
        vm.SummaryText.Should().Be("2 images: 1 override, 1 rename, 1 ignore");
    }

    [Fact]
    public void WhenEverythingIsIgnoredThenSummaryTotalIsZero()
    {
        var vm = CreateVm(
            Item("a.png", FileConflictResolution.Ignore),
            Item("b.png", FileConflictResolution.Ignore));

        vm.SummaryText.Should().Be("0 images: 2 ignore");
    }

    #endregion

    #region FormatFileSize (duplicated in FileConflictItem and NonConflictingFileItem)

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048575L, "1024.0 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1073741823L, "1024.0 MB")]
    [InlineData(1073741824L, "1.00 GB")]
    [InlineData(2147483648L, "2.00 GB")]
    public void WhenFormattingConflictItemSizesThenUnitBoundariesAreRespected(long bytes, string expected)
    {
        WithInvariantCulture(() =>
        {
            var item = new FileConflictItem
            {
                ConflictingName = "a.png",
                ExistingFileSize = bytes,
                NewFileSize = bytes
            };

            item.ExistingFileSizeText.Should().Be(expected);
            item.NewFileSizeText.Should().Be(expected);
        });
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1048575L, "1024.0 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1073741823L, "1024.0 MB")]
    [InlineData(1073741824L, "1.00 GB")]
    public void WhenFormattingNonConflictingItemSizesThenTheDuplicateFormatterMatches(long bytes, string expected)
    {
        WithInvariantCulture(() =>
        {
            var item = new NonConflictingFileItem { FileName = "a.png", FileSize = bytes };
            item.FileSizeText.Should().Be(expected);
        });
    }

    #endregion

    #region FileConflictItem helpers

    [Fact]
    public void WhenCreationDatesAreSetThenTheyAreFormattedWithDotSeparators()
    {
        // "dd.MM.yyyy" uses literal dots, so the result is culture-stable.
        var item = new FileConflictItem
        {
            ConflictingName = "a.png",
            ExistingCreationDate = new DateTime(2024, 3, 5, 13, 45, 0, DateTimeKind.Utc),
            NewCreationDate = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc)
        };

        item.ExistingCreationDateText.Should().Be("05.03.2024");
        item.NewCreationDateText.Should().Be("31.12.2025");
    }

    [Fact]
    public void WhenPairedCaptionPathIsSetThenHasPairedCaptionIsTrue()
    {
        new FileConflictItem { PairedCaptionPath = @"C:\x\a.txt" }.HasPairedCaption.Should().BeTrue();
        new FileConflictItem { PairedCaptionPath = "" }.HasPairedCaption.Should().BeFalse();
        new FileConflictItem().HasPairedCaption.Should().BeFalse();
    }

    [Fact]
    public void WhenIsOverrideSetToTrueThenResolutionBecomesOverride()
    {
        var item = new FileConflictItem { ConflictingName = "a.png" };

        item.IsOverride = true;

        item.Resolution.Should().Be(FileConflictResolution.Override);
    }

    [Fact]
    public void WhenRadioFlagSetToFalseThenResolutionIsUnchanged()
    {
        // Radio-button style setters ignore the "unchecked" transition.
        var item = new FileConflictItem { ConflictingName = "a.png", Resolution = FileConflictResolution.Ignore };

        item.IsOverride = false;
        item.IsRename = false;
        item.IsIgnore = false;

        item.Resolution.Should().Be(FileConflictResolution.Ignore);
    }

    [Fact]
    public void WhenResolutionChangesThenAllThreeRadioFlagsAreNotified()
    {
        var item = new FileConflictItem { ConflictingName = "a.png" };
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.Resolution = FileConflictResolution.Ignore;

        raised.Should().Contain(nameof(FileConflictItem.Resolution));
        raised.Should().Contain(nameof(FileConflictItem.IsOverride));
        raised.Should().Contain(nameof(FileConflictItem.IsRename));
        raised.Should().Contain(nameof(FileConflictItem.IsIgnore));
    }

    #endregion

    #region FileConflictResolutionResult

    [Fact]
    public void WhenCancelledThenResultIsNotConfirmedAndHasNoConflicts()
    {
        var result = FileConflictResolutionResult.Cancelled();

        result.Confirmed.Should().BeFalse();
        result.Conflicts.Should().BeEmpty();
    }

    #endregion
}
