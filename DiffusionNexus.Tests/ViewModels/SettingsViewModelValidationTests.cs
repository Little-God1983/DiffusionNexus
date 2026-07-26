using System.Reflection;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Unit tests for the two pure backup validators on <see cref="SettingsViewModel"/>:
/// <c>ValidateAutoBackupLocation</c> and <c>ValidateBackupSettings</c>.
/// <para>
/// Both are private, so they are reached through reflection (the same pattern as
/// <c>StableDiffusionCppLoaderTests</c>). Only the two required constructor
/// dependencies are mocked; nothing here touches persistence, the dialog service
/// or the UI thread.
/// </para>
/// </summary>
public class SettingsViewModelValidationTests
{
    private const string SameFolderError =
        "Backup location cannot be the same as the Dataset Storage folder.";

    private const string SubfolderError =
        "Backup location cannot be a subfolder of the Dataset Storage folder.";

    private const string LocationRequiredError =
        "Backup location is required when automatic backup is enabled.";

    private const string IntervalError =
        "Backup interval must be at least 1 hour or 1 day.";

    private const string ValidationStatus =
        "Please fix the validation errors before saving.";

    private static readonly MethodInfo ValidateLocationMethod =
        typeof(SettingsViewModel).GetMethod(
            "ValidateAutoBackupLocation",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ValidateAutoBackupLocation not found.");

    private static readonly MethodInfo ValidateBackupSettingsMethod =
        typeof(SettingsViewModel).GetMethod(
            "ValidateBackupSettings",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ValidateBackupSettings not found.");

    private static SettingsViewModel CreateVm()
        => new(new Mock<IAppSettingsService>().Object, new Mock<ISecureStorage>().Object);

    private static void ValidateLocation(SettingsViewModel vm)
        => ValidateLocationMethod.Invoke(vm, null);

    private static bool ValidateBackupSettings(SettingsViewModel vm)
        => (bool)ValidateBackupSettingsMethod.Invoke(vm, null)!;

    /// <summary>A rooted folder under the temp directory; nothing is created on disk.</summary>
    private static string Root(params string[] segments)
        => Path.Combine(new[] { Path.GetTempPath(), "dn-settings-validation" }.Concat(segments).ToArray());

    #region ValidateAutoBackupLocation - null / empty inputs

    [Fact]
    public void WhenBothPathsAreNullThenNoLocationErrorIsProduced()
    {
        var vm = CreateVm();

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    [Fact]
    public void WhenOnlyTheBackupPathIsSetThenNoLocationErrorIsProduced()
    {
        var vm = CreateVm();
        vm.AutoBackupLocation = Root("backups");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    [Fact]
    public void WhenOnlyTheStoragePathIsSetThenNoLocationErrorIsProduced()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("storage");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void WhenTheBackupPathIsBlankThenValidationIsSkipped(string blank)
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("storage");
        vm.AutoBackupLocation = blank;

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenTheStoragePathIsBlankThenValidationIsSkipped(string blank)
    {
        var vm = CreateVm();
        vm.AutoBackupLocation = Root("backups");
        vm.DatasetStoragePath = blank;

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    #endregion

    #region ValidateAutoBackupLocation - identical folders

    [Fact]
    public void WhenBackupEqualsStorageThenTheSameFolderErrorIsSet()
    {
        var vm = CreateVm();
        var path = Root("storage");
        vm.DatasetStoragePath = path;
        vm.AutoBackupLocation = path;

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().Be(SameFolderError);
    }

    [Fact]
    public void WhenBackupEqualsStorageIgnoringCaseThenTheSameFolderErrorIsSet()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("Storage");
        vm.AutoBackupLocation = Root("STORAGE");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().Be(SameFolderError);
    }

    [Fact]
    public void WhenTheBackupPathHasATrailingSeparatorThenItStillCountsAsTheSameFolder()
    {
        var vm = CreateVm();
        var path = Root("storage");
        vm.DatasetStoragePath = path;
        vm.AutoBackupLocation = path + Path.DirectorySeparatorChar;

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().Be(SameFolderError);
    }

    [Fact]
    public void WhenBothPathsHaveTrailingSeparatorsThenTheyStillCompareEqual()
    {
        var vm = CreateVm();
        var path = Root("storage");
        vm.DatasetStoragePath = path + Path.DirectorySeparatorChar;
        vm.AutoBackupLocation = path + Path.DirectorySeparatorChar;

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().Be(SameFolderError);
    }

    [Fact]
    public void WhenRelativePathsResolveToTheSameFolderThenTheSameFolderErrorIsSet()
    {
        // Both sides go through Path.GetFullPath, so equivalent relative
        // spellings of one folder are detected regardless of the process CWD.
        var vm = CreateVm();
        vm.DatasetStoragePath = "backup-target";
        vm.AutoBackupLocation = Path.Combine(".", "backup-target");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().Be(SameFolderError);
    }

    [Fact]
    public void WhenADotDotSegmentResolvesToTheStorageFolderThenItIsStillRejected()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("storage");
        vm.AutoBackupLocation = Root("storage", "child", "..");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().Be(SameFolderError);
    }

    #endregion

    #region ValidateAutoBackupLocation - nesting

    [Fact]
    public void WhenBackupIsDirectlyInsideStorageThenTheSubfolderErrorIsSet()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("storage");
        vm.AutoBackupLocation = Root("storage", "backups");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().Be(SubfolderError);
    }

    [Fact]
    public void WhenBackupIsDeeplyNestedInsideStorageThenTheSubfolderErrorIsSet()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("storage");
        vm.AutoBackupLocation = Root("storage", "a", "b", "c", "backups");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().Be(SubfolderError);
    }

    [Fact]
    public void WhenTheNestedBackupPathDiffersInCaseThenTheSubfolderErrorIsStillSet()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("Storage");
        vm.AutoBackupLocation = Root("STORAGE", "Backups");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().Be(SubfolderError);
    }

    [Fact]
    public void WhenTheBackupFolderNameMerelyStartsWithTheStorageFolderNameThenItIsAllowed()
    {
        // "…/storage_backups" must not be mistaken for a child of "…/storage";
        // the separator is appended before the prefix test precisely for this.
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("storage");
        vm.AutoBackupLocation = Root("storage_backups");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    [Fact]
    public void WhenTheBackupFolderIsASiblingThenItIsAllowed()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("storage");
        vm.AutoBackupLocation = Root("backups");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    [Fact]
    public void WhenStorageIsNestedUnderTheBackupFolderThenItIsAllowed()
    {
        // Only backup-inside-storage is rejected; the reverse is legal.
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("backups", "storage");
        vm.AutoBackupLocation = Root("backups");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    [Fact]
    public void WhenADotDotSegmentEscapesTheStorageFolderThenItIsAllowed()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("storage");
        vm.AutoBackupLocation = Root("storage", "..", "backups");

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    [Fact]
    public void WhenAPreviousErrorExistsAndThePathsBecomeValidThenTheErrorIsCleared()
    {
        var vm = CreateVm();
        var path = Root("storage");
        vm.DatasetStoragePath = path;
        vm.AutoBackupLocation = path;
        ValidateLocation(vm);
        vm.AutoBackupLocationError.Should().Be(SameFolderError);

        vm.AutoBackupLocation = Root("backups");
        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    [Fact]
    public void WhenThePathsBecomeBlankThenAPreviousErrorIsCleared()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = null;
        vm.AutoBackupLocation = null;
        vm.AutoBackupLocationError = "stale error";

        ValidateLocation(vm);

        vm.AutoBackupLocationError.Should().BeNull();
    }

    #endregion

    #region ValidateBackupSettings - disabled

    [Fact]
    public void WhenBothBackupTypesAreDisabledThenValidationPassesWithoutErrors()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = false;
        vm.BackupDatasetImagesEnabled = false;
        vm.AutoBackupIntervalDays = 0;
        vm.AutoBackupIntervalHours = 0;
        vm.AutoBackupLocation = null;

        var result = ValidateBackupSettings(vm);

        result.Should().BeTrue();
        vm.AutoBackupLocationError.Should().BeNull();
        vm.AutoBackupIntervalError.Should().BeNull();
        vm.StatusMessage.Should().BeNull();
    }

    [Fact]
    public void WhenBackupIsDisabledThenAnyBackupEnabledIsFalse()
    {
        var vm = CreateVm();

        // The database backup is on by default.
        vm.AnyBackupEnabled.Should().BeTrue();

        vm.BackupDatabaseEnabled = false;

        vm.AnyBackupEnabled.Should().BeFalse();
    }

    #endregion

    #region ValidateBackupSettings - enabled

    [Fact]
    public void WhenBackupIsEnabledWithoutALocationThenValidationFails()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.AutoBackupLocation = null;

        var result = ValidateBackupSettings(vm);

        result.Should().BeFalse();
        vm.AutoBackupLocationError.Should().Be(LocationRequiredError);
        vm.StatusMessage.Should().Be(ValidationStatus);
    }

    [Fact]
    public void WhenOnlyDatasetImageBackupIsEnabledThenTheLocationIsStillRequired()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = false;
        vm.BackupDatasetImagesEnabled = true;
        vm.AutoBackupLocation = null;

        var result = ValidateBackupSettings(vm);

        result.Should().BeFalse();
        vm.AutoBackupLocationError.Should().Be(LocationRequiredError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenTheLocationIsBlankThenItCountsAsMissing(string blank)
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.AutoBackupLocation = blank;

        var result = ValidateBackupSettings(vm);

        result.Should().BeFalse();
        vm.AutoBackupLocationError.Should().Be(LocationRequiredError);
    }

    [Fact]
    public void WhenBackupIsEnabledWithAValidLocationAndIntervalThenValidationPasses()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.DatasetStoragePath = Root("storage");
        vm.AutoBackupLocation = Root("backups");
        vm.AutoBackupIntervalDays = 1;
        vm.AutoBackupIntervalHours = 0;

        var result = ValidateBackupSettings(vm);

        result.Should().BeTrue();
        vm.AutoBackupLocationError.Should().BeNull();
        vm.AutoBackupIntervalError.Should().BeNull();
        vm.StatusMessage.Should().BeNull();
    }

    [Fact]
    public void WhenTheLocationIsInsideTheStorageFolderThenTheNestedPathErrorPropagates()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.DatasetStoragePath = Root("storage");
        vm.AutoBackupLocation = Root("storage", "backups");

        var result = ValidateBackupSettings(vm);

        result.Should().BeFalse();
        vm.AutoBackupLocationError.Should().Be(SubfolderError);
        vm.StatusMessage.Should().Be(ValidationStatus);
    }

    [Fact]
    public void WhenTheLocationEqualsTheStorageFolderThenTheSameFolderErrorPropagates()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        var path = Root("storage");
        vm.DatasetStoragePath = path;
        vm.AutoBackupLocation = path;

        var result = ValidateBackupSettings(vm);

        result.Should().BeFalse();
        vm.AutoBackupLocationError.Should().Be(SameFolderError);
    }

    #endregion

    #region ValidateBackupSettings - interval

    [Fact]
    public void WhenBothIntervalComponentsAreZeroThenValidationFails()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.AutoBackupLocation = Root("backups");
        vm.AutoBackupIntervalDays = 0;
        vm.AutoBackupIntervalHours = 0;

        var result = ValidateBackupSettings(vm);

        result.Should().BeFalse();
        vm.AutoBackupIntervalError.Should().Be(IntervalError);
        vm.AutoBackupLocationError.Should().BeNull();
        vm.StatusMessage.Should().Be(ValidationStatus);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(30, 23)]
    public void WhenAtLeastOneIntervalComponentIsPositiveThenTheIntervalIsAccepted(int days, int hours)
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.AutoBackupLocation = Root("backups");
        vm.AutoBackupIntervalDays = days;
        vm.AutoBackupIntervalHours = hours;

        var result = ValidateBackupSettings(vm);

        result.Should().BeTrue();
        vm.AutoBackupIntervalError.Should().BeNull();
    }

    [Fact]
    public void WhenBothTheLocationAndIntervalAreInvalidThenBothErrorsAreReported()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.AutoBackupLocation = null;
        vm.AutoBackupIntervalDays = 0;
        vm.AutoBackupIntervalHours = 0;

        var result = ValidateBackupSettings(vm);

        result.Should().BeFalse();
        vm.AutoBackupLocationError.Should().Be(LocationRequiredError);
        vm.AutoBackupIntervalError.Should().Be(IntervalError);
        vm.StatusMessage.Should().Be(ValidationStatus);
    }

    #endregion

    #region ValidateBackupSettings - error and status lifecycle

    [Fact]
    public void WhenValidationRunsThenStaleErrorsFromAPreviousRunAreCleared()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.AutoBackupLocation = Root("backups");
        vm.AutoBackupIntervalDays = 1;
        vm.AutoBackupLocationError = "stale location error";
        vm.AutoBackupIntervalError = "stale interval error";

        var result = ValidateBackupSettings(vm);

        result.Should().BeTrue();
        vm.AutoBackupLocationError.Should().BeNull();
        vm.AutoBackupIntervalError.Should().BeNull();
    }

    [Fact]
    public void WhenValidationSucceedsThenAnExistingStatusMessageIsLeftAlone()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.AutoBackupLocation = Root("backups");
        vm.AutoBackupIntervalDays = 1;
        vm.StatusMessage = "Settings saved successfully.";

        var result = ValidateBackupSettings(vm);

        result.Should().BeTrue();
        vm.StatusMessage.Should().Be("Settings saved successfully.");
    }

    [Fact]
    public void WhenValidationSucceedsAfterAFailureThenTheErrorsGoAway()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.AutoBackupLocation = null;
        ValidateBackupSettings(vm).Should().BeFalse();

        vm.AutoBackupLocation = Root("backups");
        var result = ValidateBackupSettings(vm);

        result.Should().BeTrue();
        vm.AutoBackupLocationError.Should().BeNull();
        vm.AutoBackupIntervalError.Should().BeNull();
    }

    [Fact]
    public void WhenBackupIsTurnedOffThenPendingValidationErrorsAreCleared()
    {
        var vm = CreateVm();
        vm.BackupDatabaseEnabled = true;
        vm.AutoBackupLocation = null;
        vm.AutoBackupIntervalDays = 0;
        vm.AutoBackupIntervalHours = 0;
        ValidateBackupSettings(vm).Should().BeFalse();
        vm.AutoBackupLocationError.Should().NotBeNull();
        vm.AutoBackupIntervalError.Should().NotBeNull();

        vm.BackupDatabaseEnabled = false;

        vm.AutoBackupLocationError.Should().BeNull();
        vm.AutoBackupIntervalError.Should().BeNull();
    }

    #endregion

    #region Property-change side effects used by the validators

    [Fact]
    public void WhenTheBackupLocationIsAssignedThenValidationRunsAutomatically()
    {
        var vm = CreateVm();
        vm.DatasetStoragePath = Root("storage");

        vm.AutoBackupLocation = Root("storage", "inside");

        vm.AutoBackupLocationError.Should().Be(SubfolderError);
        vm.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void WhenTheStoragePathIsAssignedThenValidationRunsAutomatically()
    {
        var vm = CreateVm();
        vm.AutoBackupLocation = Root("storage", "inside");
        vm.AutoBackupLocationError.Should().BeNull();

        vm.DatasetStoragePath = Root("storage");

        vm.AutoBackupLocationError.Should().Be(SubfolderError);
    }

    [Fact]
    public void WhenAnIntervalComponentIsAssignedThenTheIntervalErrorIsCleared()
    {
        var vm = CreateVm();
        vm.AutoBackupIntervalError = "stale";

        vm.AutoBackupIntervalDays = 2;

        vm.AutoBackupIntervalError.Should().BeNull();

        vm.AutoBackupIntervalError = "stale again";
        vm.AutoBackupIntervalHours = 5;

        vm.AutoBackupIntervalError.Should().BeNull();
    }

    #endregion
}
