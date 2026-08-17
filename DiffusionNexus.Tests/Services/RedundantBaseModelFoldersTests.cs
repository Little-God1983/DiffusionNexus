using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.Services.Diffusion;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Covers <see cref="RedundantBaseModelFolders"/> — the one-time cleanup that removes the
/// per-category rows the pre-fix registrar created. Every guard here exists because the
/// alternative is deleting settings the user meant to keep.
/// </summary>
public sealed class RedundantBaseModelFoldersTests
{
    private const int PackageId = 6;

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> Roots =
        new Dictionary<int, IReadOnlyList<string>>
        {
            [PackageId] = [@"D:\Models", @"E:\AI\ComfyUI\models"],
        };

    private static BaseModelFolder Row(
        int id,
        string path,
        int? packageId = PackageId,
        bool isDefault = false)
        => new() { Id = id, FolderPath = path, InstallerPackageId = packageId, IsDefault = isDefault };

    [Fact]
    public void Resolve_RemovesCategoryFoldersNestedInARoot()
    {
        // The exact shape found in the wild: one real root plus its category folders.
        BaseModelFolder[] rows =
        [
            Row(1, @"D:\Models"),
            Row(2, @"D:\Models\Lora"),
            Row(3, @"D:\Models\VAE"),
            Row(4, @"D:\Models\LLM\GGUF"),
            Row(5, @"E:\AI\ComfyUI\models"),
        ];

        var redundant = RedundantBaseModelFolders.Resolve(rows, Roots);

        redundant.Select(r => r.Id).Should().Equal(2, 3, 4);
    }

    [Fact]
    public void Resolve_KeepsTheRootsThemselves()
    {
        BaseModelFolder[] rows = [Row(1, @"D:\Models"), Row(2, @"E:\AI\ComfyUI\models")];

        RedundantBaseModelFolders.Resolve(rows, Roots).Should().BeEmpty();
    }

    [Fact]
    public void Resolve_KeepsRootsSpelledWithATrailingSeparator()
    {
        // The registrar and the stored row can disagree about the trailing separator; that
        // must not turn a root into a "nested" folder and delete it.
        BaseModelFolder[] rows = [Row(1, @"D:\Models\")];

        RedundantBaseModelFolders.Resolve(rows, Roots).Should().BeEmpty();
    }

    [Fact]
    public void Resolve_NeverTouchesManuallyAddedRows()
    {
        // No FK means the user added it in Settings. Its shape is none of our business.
        BaseModelFolder[] rows = [Row(1, @"D:\Models\Lora", packageId: null)];

        RedundantBaseModelFolders.Resolve(rows, Roots).Should().BeEmpty(
            "a folder the user added by hand is never pruned");
    }

    [Fact]
    public void Resolve_NeverTouchesTheDefaultDownloadTarget()
    {
        BaseModelFolder[] rows = [Row(1, @"D:\Models\Lora", isDefault: true)];

        RedundantBaseModelFolders.Resolve(rows, Roots).Should().BeEmpty(
            "losing the default download target silently is worse than one redundant row");
    }

    [Fact]
    public void Resolve_KeepsRowsOutsideEveryKnownRoot()
    {
        // Could be a stale root whose folder moved — deleting it would lose a real setting.
        BaseModelFolder[] rows = [Row(1, @"F:\SomewhereElse")];

        RedundantBaseModelFolders.Resolve(rows, Roots).Should().BeEmpty();
    }

    [Fact]
    public void Resolve_PrunesRowsLeftBehindByAReAddedInstallation()
    {
        // Taken from a real database: the junk rows carry InstallerPackageId 6, but the
        // installation was removed and re-added as id 36, so no live package has that id.
        // Requiring the ids to match would freeze the mess in place exactly where it happened.
        var liveRoots = new Dictionary<int, IReadOnlyList<string>>
        {
            [36] = [@"E:\AI\ComfyUI\models", @"D:\Models"],
            [17] = [@"E:\Installer\6\ComfyUI\models", @"D:\Matrix\Models"],
        };

        BaseModelFolder[] rows =
        [
            Row(1, @"E:\AI\ComfyUI\models", packageId: 6),
            Row(2, @"E:\Installer\6\ComfyUI\models", packageId: 17),
            Row(3, @"D:\Models", packageId: 6),
            Row(4, @"D:\Models\StableDiffusion", packageId: 6),
            Row(5, @"D:\Models\TextEncoders", packageId: 6),
            Row(8, @"D:\Models\Lora", packageId: 6),
            Row(19, @"D:\Models\LLM\GGUF", packageId: 6),
        ];

        var redundant = RedundantBaseModelFolders.Resolve(rows, liveRoots);

        redundant.Select(r => r.Id).Should().Equal(4, 5, 8, 19);
        redundant.Should().NotContain(r => r.Id == 1, "the installation's own models/ folder is a root");
        redundant.Should().NotContain(r => r.Id == 3, "D:\\Models is the shared base_path — a real root");
    }

    [Fact]
    public void Resolve_OrphanedRow_ThatIsARootOfAnotherInstallation_Survives()
    {
        var liveRoots = new Dictionary<int, IReadOnlyList<string>>
        {
            [36] = [@"D:\Models"],
            [17] = [@"D:\Models\Shared"],
        };

        // Nested inside 36's root, but it is 17's root in its own right.
        BaseModelFolder[] rows = [Row(1, @"D:\Models\Shared", packageId: 6)];

        RedundantBaseModelFolders.Resolve(rows, liveRoots).Should().BeEmpty();
    }

    [Fact]
    public void Resolve_KeepsEverything_WhenTheInstallationContributesNoRoots()
    {
        // An unplugged drive or a temporarily missing install resolves to zero roots. That
        // is not evidence its registrations are junk.
        BaseModelFolder[] rows = [Row(1, @"D:\Models\Lora")];

        RedundantBaseModelFolders.Resolve(rows, new Dictionary<int, IReadOnlyList<string>>())
            .Should().BeEmpty();

        RedundantBaseModelFolders.Resolve(rows, new Dictionary<int, IReadOnlyList<string>>
        {
            [PackageId] = [],
        }).Should().BeEmpty();
    }

    [Fact]
    public void Resolve_RowOfAnUnknownPackage_IsJudgedAgainstTheLiveRoots()
    {
        // Package 99 does not exist any more; the folder is still provably a category folder
        // inside a live root, so it goes.
        BaseModelFolder[] rows = [Row(1, @"D:\Models\Lora", packageId: 99)];

        RedundantBaseModelFolders.Resolve(rows, Roots).Select(r => r.Id).Should().Equal(1);
    }

    [Fact]
    public void Resolve_EmptyInput_IsEmpty()
    {
        RedundantBaseModelFolders.Resolve([], Roots).Should().BeEmpty();
    }
}
