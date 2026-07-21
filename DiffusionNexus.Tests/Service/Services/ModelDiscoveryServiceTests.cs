using DiffusionNexus.Service.Helper;
using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Service.Services;

/// <summary>
/// Covers issue #435: BuildFolderTree used to re-enumerate every subtree once per ancestor
/// node (O(n x depth)), and had no guard against unreadable subdirectories aborting the whole
/// scan. These tests pin the ModelCount rollup semantics (own files + all descendants), assert
/// the fix touches each file exactly once (regression guard for the O(n x depth) blowup), and
/// verify an inaccessible subtree is skipped rather than crashing the scan.
/// </summary>
public class ModelDiscoveryServiceTests : IDisposable
{
    private readonly string _root;

    public ModelDiscoveryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ModelDiscoveryServiceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    void IDisposable.Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static void CreateFiles(string directory, params string[] fileNames)
    {
        Directory.CreateDirectory(directory);
        foreach (var name in fileNames)
        {
            File.WriteAllText(Path.Combine(directory, name), string.Empty);
        }
    }

    // ---- ModelCount rollup semantics (pin correctness; these already pass against the
    // unmodified implementation too, since the old AllDirectories-per-node approach happens to
    // produce the right totals - just wastefully. Written first to lock in the contract before
    // refactoring the counting strategy.) ----

    [Fact]
    public void BuildFolderTree_LeafFolderWithModelFiles_CountsOwnFilesOnly()
    {
        CreateFiles(_root, "a.safetensors", "b.pt", "notes.txt");

        var node = new ModelDiscoveryService().BuildFolderTree(_root);

        node.ModelCount.Should().Be(2);
        node.Children.Should().BeEmpty();
        node.IsAccessible.Should().BeTrue();
    }

    [Fact]
    public void BuildFolderTree_ThreeLevelChain_RollsUpCountsFromLeafToRoot()
    {
        CreateFiles(_root, "root.ckpt", "root_readme.txt");
        var child = Path.Combine(_root, "child");
        CreateFiles(child, "child1.safetensors", "child2.pt");
        var grandchild = Path.Combine(child, "grandchild");
        CreateFiles(grandchild, "g1.pth", "g2.ckpt", "g3.safetensors");

        var node = new ModelDiscoveryService().BuildFolderTree(_root);

        node.ModelCount.Should().Be(6); // 1 own + 2 child + 3 grandchild
        node.Children.Should().ContainSingle();
        var childNode = node.Children.Single();
        childNode.Name.Should().Be("child");
        childNode.ModelCount.Should().Be(5); // 2 own + 3 grandchild

        childNode.Children.Should().ContainSingle();
        var grandchildNode = childNode.Children.Single();
        grandchildNode.Name.Should().Be("grandchild");
        grandchildNode.ModelCount.Should().Be(3);
        grandchildNode.Children.Should().BeEmpty();
    }

    [Fact]
    public void BuildFolderTree_SiblingSubtrees_SumIndependentlyWithoutLeaking()
    {
        CreateFiles(_root, "own.safetensors");
        var subA = Path.Combine(_root, "A");
        CreateFiles(subA, "a1.pt", "a2.pt");
        var subB = Path.Combine(_root, "B");
        CreateFiles(subB, "b1.ckpt", "b2.ckpt", "b3.ckpt");

        var node = new ModelDiscoveryService().BuildFolderTree(_root);

        node.ModelCount.Should().Be(6); // 1 + 2 + 3
        node.Children.Should().HaveCount(2);
        node.Children.Single(c => c.Name == "A").ModelCount.Should().Be(2);
        node.Children.Single(c => c.Name == "B").ModelCount.Should().Be(3);
    }

    [Fact]
    public void BuildFolderTree_EmptyDirectory_ReturnsZeroCountAndNoChildren()
    {
        var node = new ModelDiscoveryService().BuildFolderTree(_root);

        node.ModelCount.Should().Be(0);
        node.Children.Should().BeEmpty();
        node.IsAccessible.Should().BeTrue();
    }

    [Fact]
    public void BuildFolderTree_OnlyCountsRecognizedModelExtensions()
    {
        // One file per real model extension, driven off StaticFileTypes itself so this pins
        // "whatever the current filter predicate is" rather than a hardcoded duplicate list.
        var modelFiles = StaticFileTypes.ModelExtensions.Select((ext, i) => $"model{i}{ext}").ToArray();
        CreateFiles(_root, modelFiles);
        CreateFiles(_root, "notes.txt", "data.json", "cover.png", "workflow.yaml");

        var node = new ModelDiscoveryService().BuildFolderTree(_root);

        node.ModelCount.Should().Be(StaticFileTypes.ModelExtensions.Length);
    }

    [Fact]
    public void BuildFolderTree_ExtensionMatchIsCaseInsensitive()
    {
        CreateFiles(_root, "Loud.SAFETENSORS", "quiet.PtH");

        var node = new ModelDiscoveryService().BuildFolderTree(_root);

        node.ModelCount.Should().Be(2);
    }

    // ---- Efficiency: each file must be touched exactly once across the whole build, not once
    // per ancestor. TRUE RED before the fix: with the seam wired to the original AllDirectories
    // strategy (one recursive re-scan per node), a 4-level chain with 1 file per level touched
    // 10 file-entries (4+3+2+1) instead of the 4 actual files. After switching BuildNode to a
    // single-pass rollup (TopDirectoryOnly + accumulate), this reports exactly 4. ----

    [Fact]
    public void BuildFolderTree_TouchesEachFileExactlyOnce_NotOncePerAncestor()
    {
        var level0 = _root;
        var level1 = Path.Combine(level0, "L1");
        var level2 = Path.Combine(level1, "L2");
        var level3 = Path.Combine(level2, "L3");
        CreateFiles(level0, "f0.safetensors");
        CreateFiles(level1, "f1.safetensors");
        CreateFiles(level2, "f2.safetensors");
        CreateFiles(level3, "f3.safetensors");
        const int totalActualFiles = 4;

        var totalFilesTouched = 0;
        var service = new ModelDiscoveryService(
            dir => dir.GetDirectories(),
            (path, option) =>
            {
                var results = Directory.EnumerateFiles(path, "*", option).ToArray();
                totalFilesTouched += results.Length;
                return results;
            });

        var node = service.BuildFolderTree(_root);

        node.ModelCount.Should().Be(totalActualFiles);
        totalFilesTouched.Should().Be(totalActualFiles,
            "each file's directory should be enumerated exactly once, not once per ancestor node");
    }

    // ---- Unreadable subdirectory guard: TRUE RED before the fix - the original code had no
    // try/catch anywhere, so any UnauthorizedAccessException/DirectoryNotFoundException/IOException
    // raised while listing a subtree propagated out of BuildFolderTree and aborted the entire scan. ----

    [Fact]
    public void BuildFolderTree_SubdirectoryListingThrowsUnauthorizedAccess_SkipsSubtreeInsteadOfAborting()
    {
        CreateFiles(_root, "own.safetensors");
        var accessible = Path.Combine(_root, "Accessible");
        CreateFiles(accessible, "a1.pt", "a2.pt");
        var restrictedPath = Path.Combine(_root, "Restricted");
        Directory.CreateDirectory(restrictedPath); // exists on disk; enumeration is faked to fail

        var service = new ModelDiscoveryService(
            dir => dir.FullName == restrictedPath
                ? throw new UnauthorizedAccessException("simulated access-denied subtree")
                : dir.GetDirectories(),
            (path, option) => Directory.EnumerateFiles(path, "*", option));

        FolderNode? node = null;
        Action act = () => node = service.BuildFolderTree(_root);

        act.Should().NotThrow();
        node!.ModelCount.Should().Be(3); // 1 own + 2 Accessible; Restricted contributes 0
        node.IsAccessible.Should().BeTrue();

        var accessibleNode = node.Children.Single(c => c.Name == "Accessible");
        accessibleNode.ModelCount.Should().Be(2);
        accessibleNode.IsAccessible.Should().BeTrue();

        var restrictedNode = node.Children.Single(c => c.Name == "Restricted");
        restrictedNode.ModelCount.Should().Be(0);
        restrictedNode.IsAccessible.Should().BeFalse();
        restrictedNode.Children.Should().BeEmpty();
    }

    [Fact]
    public void BuildFolderTree_FileEnumerationThrowsDirectoryNotFound_SkipsOwnCountInsteadOfAborting()
    {
        // Simulates a broken junction/reparse point: listing subdirectories succeeds (or there are
        // none), but reading the directory's own files fails.
        CreateFiles(_root, "own.safetensors");
        var brokenJunction = Path.Combine(_root, "BrokenJunction");
        Directory.CreateDirectory(brokenJunction);

        var service = new ModelDiscoveryService(
            dir => dir.GetDirectories(),
            (path, option) => path == brokenJunction
                ? throw new DirectoryNotFoundException("simulated broken junction")
                : Directory.EnumerateFiles(path, "*", option));

        FolderNode? node = null;
        Action act = () => node = service.BuildFolderTree(_root);

        act.Should().NotThrow();
        node!.ModelCount.Should().Be(1); // only the root's own file; BrokenJunction contributes 0
        var brokenNode = node.Children.Single(c => c.Name == "BrokenJunction");
        brokenNode.ModelCount.Should().Be(0);
        brokenNode.IsAccessible.Should().BeFalse();
    }

    [Fact]
    public void BuildFolderTree_RootItselfUnreadable_ReturnsInaccessibleRootWithoutThrowing()
    {
        var service = new ModelDiscoveryService(
            dir => throw new UnauthorizedAccessException("simulated - root itself unreadable"),
            (path, option) => Directory.EnumerateFiles(path, "*", option));

        FolderNode? node = null;
        Action act = () => node = service.BuildFolderTree(_root);

        act.Should().NotThrow();
        node!.ModelCount.Should().Be(0);
        node.IsAccessible.Should().BeFalse();
        node.Children.Should().BeEmpty();
    }

    [Fact]
    public void BuildFolderTree_UnrelatedException_IsNotSwallowed()
    {
        // Guards against an overly broad catch: only filesystem-access-shaped exceptions should
        // be treated as "skip this subtree"; anything else is a real bug and must propagate.
        var service = new ModelDiscoveryService(
            dir => throw new InvalidOperationException("not a filesystem access problem"),
            (path, option) => Directory.EnumerateFiles(path, "*", option));

        Action act = () => service.BuildFolderTree(_root);

        act.Should().Throw<InvalidOperationException>();
    }
}
