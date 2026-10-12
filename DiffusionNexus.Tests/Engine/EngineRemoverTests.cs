using System.Diagnostics;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public sealed class EngineRemoverTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("dn-engine-remover-");
    private readonly Mock<IManagedEngineControl> _engine = new();
    private readonly Mock<IDisposable> _hold = new();

    public EngineRemoverTests()
    {
        _engine.Setup(e => e.TryBeginMaintenanceAsync()).ReturnsAsync(_hold.Object);
        _engine.Setup(e => e.StopIfIdleAsync()).ReturnsAsync(true);
    }

    public void Dispose()
    {
        // Remove junctions first so cleanup never walks into a target.
        try
        {
            foreach (var dir in _temp.EnumerateDirectories("*", SearchOption.AllDirectories))
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(dir.FullName, recursive: false);
        }
        catch { /* best effort */ }
        try { _temp.Delete(recursive: true); } catch { /* best effort */ }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_temp.FullName, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "f.txt"), "x");
        return path;
    }

    private static void Junction(string link, string target)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        Directory.Exists(link).Should().BeTrue("the test needs the junction");
    }

    [Fact]
    public async Task AGenerationRunning_IsBusy_AndNothingIsDeleted()
    {
        var folder = Folder("Engine");
        _engine.SetupGet(e => e.HasActiveJobs).Returns(true);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([folder], []);

        result.WasBusy.Should().BeTrue();
        Directory.Exists(folder).Should().BeTrue();
        _engine.Verify(e => e.StopIfIdleAsync(), Times.Never);
    }

    [Fact]
    public async Task AnInstallRunning_IsBusy()
    {
        var activity = new InstallActivity();
        using var _ = activity.Begin("Diffusion Nexus Engine features");

        var result = await new EngineRemover(_engine.Object, activity).RemoveAsync([Folder("Engine")], []);

        result.BusyReason.Should().Contain("Diffusion Nexus Engine features");
    }

    // I1: Uninstall Engine checks the installs itself and then holds its own handle; that handle must not
    // make the removal refuse itself. A running generation still does.
    [Fact]
    public async Task TheCallersOwnInstallActivity_DoesNotRefuse_AGenerationStillDoes()
    {
        var activity = new InstallActivity();
        using var _ = activity.Begin("Diffusion Nexus Engine uninstall");
        var folder = Folder("Engine");

        var result = await new EngineRemover(_engine.Object, activity).RemoveAsync([folder], [], installActivityHeldByCaller: true);

        result.WasBusy.Should().BeFalse();
        Directory.Exists(folder).Should().BeFalse();

        var other = Folder("Engine2");
        _engine.SetupGet(e => e.HasActiveJobs).Returns(true);
        (await new EngineRemover(_engine.Object, activity).RemoveAsync([other], [], installActivityHeldByCaller: true))
            .BusyReason.Should().Be("a generation is running on it");
    }

    [Fact]
    public async Task ARunningIdleEngine_IsStoppedFirst_ThenDeletedUnderTheHold()
    {
        var folder = Folder("Engine");
        _engine.SetupGet(e => e.IsRunning).Returns(true);
        var order = new List<string>();
        _engine.Setup(e => e.StopIfIdleAsync()).Callback(() => order.Add("stop")).ReturnsAsync(true);
        _engine.Setup(e => e.TryBeginMaintenanceAsync()).Callback(() => order.Add("hold")).ReturnsAsync(_hold.Object);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([folder], []);

        order.Should().Equal("stop", "hold");
        result.Failed.Should().BeEmpty();
        Directory.Exists(folder).Should().BeFalse();
        _hold.Verify(h => h.Dispose(), Times.Once);
    }

    // Review focus 4: the Engine started between the check and the hold.
    [Fact]
    public async Task AHoldThatIsRefused_IsBusy_AndNothingIsDeleted()
    {
        var folder = Folder("Engine");
        _engine.Setup(e => e.TryBeginMaintenanceAsync()).ReturnsAsync((IDisposable?)null);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([folder], []);

        result.WasBusy.Should().BeTrue();
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public async Task ALockedFile_IsReported_AndTheRestIsDeleted()
    {
        var a = Path.Combine(_temp.FullName, "a.bin");
        var b = Path.Combine(_temp.FullName, "b.bin");
        File.WriteAllText(a, "x");
        File.WriteAllText(b, "x");
        using var locked = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([], [a, b]);

        result.Failed.Should().ContainSingle().Which.Path.Should().Be(a);
        File.Exists(b).Should().BeFalse();
    }

    // I2: one locked file in the middle of a folder does not stop the walk: everything else goes, the
    // locked file is reported (once, not also its parent folders), and the folder stays only around it.
    [Fact]
    public async Task ALockedFileInTheMiddleOfAFolder_IsReported_AndEverythingElseIsDeleted()
    {
        var engine = Path.Combine(_temp.FullName, "Engine");
        var nodes = Path.Combine(engine, "custom_nodes");
        Directory.CreateDirectory(Path.Combine(nodes, "c_pack"));
        Directory.CreateDirectory(Path.Combine(engine, "z_models"));
        var a = Path.Combine(nodes, "a.py");
        var b = Path.Combine(nodes, "b.py");
        var c = Path.Combine(nodes, "c_pack", "node.py");
        var d = Path.Combine(nodes, "d.py");
        var z = Path.Combine(engine, "z_models", "m.safetensors");
        foreach (var file in new[] { a, b, c, d, z }) File.WriteAllText(file, "x");
        File.SetAttributes(d, FileAttributes.ReadOnly);

        EngineRemovalResult result;
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
            result = await new EngineRemover(_engine.Object, null).RemoveAsync([engine], [], engine);

        result.Failed.Should().ContainSingle().Which.Path.Should().Be(b);
        File.Exists(b).Should().BeTrue();
        foreach (var gone in new[] { a, c, d, z }) File.Exists(gone).Should().BeFalse(gone);
        Directory.Exists(Path.Combine(nodes, "c_pack")).Should().BeFalse();
        Directory.Exists(Path.Combine(engine, "z_models")).Should().BeFalse();
    }

    // Review focus 2: a corrupted row must never delete a drive.
    [Fact]
    public async Task ADriveRoot_IsRefused()
    {
        var fs = new Mock<IRemovalFileSystem>();

        var result = await new EngineRemover(_engine.Object, null, fileSystem: fs.Object).RemoveAsync([@"D:\"], []);

        result.Failed.Should().ContainSingle().Which.Reason.Should().Contain("drive");
        fs.Verify(f => f.DeleteDirectory(It.IsAny<string>()), Times.Never);
    }

    // Review focus 1: a junction inside the Engine pointing at the user's library loses the link only.
    [Fact]
    public async Task AJunctionInsideTheFolder_IsRemoved_ItsTargetIsKept()
    {
        var engine = Folder("Engine");
        var library = Folder("Library");
        Junction(Path.Combine(engine, "loras"), library);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([engine], []);

        result.Failed.Should().BeEmpty();
        Directory.Exists(engine).Should().BeFalse();
        File.Exists(Path.Combine(library, "f.txt")).Should().BeTrue("the user's files behind the junction stay");
    }

    // Controller ruling: a file reached through a junction lives elsewhere; deleting it would delete the user's file.
    [Fact]
    public async Task AFileReachedThroughAJunction_IsRefused_AndKept()
    {
        var engine = Folder("Engine");
        var library = Folder("Library");
        Junction(Path.Combine(engine, "models"), library);
        var viaLink = Path.Combine(engine, "models", "f.txt");
        var fs = new Mock<IRemovalFileSystem>();

        var result = await new EngineRemover(_engine.Object, null, fileSystem: fs.Object).RemoveAsync([], [viaLink]);

        result.Failed.Should().ContainSingle().Which.Path.Should().Be(viaLink);
        File.Exists(Path.Combine(library, "f.txt")).Should().BeTrue();
        fs.Verify(f => f.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WithoutAnEngine_ItStillDeletes()
    {
        var folder = Folder("Engine");

        var result = await new EngineRemover(null, null).RemoveAsync([folder], []);

        result.WasBusy.Should().BeFalse();
        Directory.Exists(folder).Should().BeFalse();
    }

    [Fact]
    public async Task ADirectoryThatIsItselfAJunction_LosesTheLinkOnly()
    {
        var engine = Folder("Engine");
        var library = Folder("Library");
        var link = Path.Combine(engine, "custom_nodes");
        Junction(link, library);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([link], [], engine);

        result.Failed.Should().BeEmpty();
        Directory.Exists(link).Should().BeFalse();
        File.Exists(Path.Combine(library, "f.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task AFolderUnderAJunctionedParentInsideTheEngine_IsRefused_AndKept()
    {
        var engine = Folder("Engine");
        var library = Folder("Library");
        Directory.CreateDirectory(Path.Combine(library, "pack"));
        File.WriteAllText(Path.Combine(library, "pack", "node.py"), "x");
        Junction(Path.Combine(engine, "custom_nodes"), library);
        var pack = Path.Combine(engine, "custom_nodes", "pack");

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([pack], [], engine);

        result.Failed.Should().ContainSingle().Which.Path.Should().Be(pack);
        File.Exists(Path.Combine(library, "pack", "node.py")).Should().BeTrue();
    }

    [Fact]
    public async Task AnEngineUnderALinkedParent_IsDeletedNormally()
    {
        var real = Folder("Real");
        Directory.CreateDirectory(Path.Combine(real, "Engine", "sub"));
        File.WriteAllText(Path.Combine(real, "Engine", "sub", "a.bin"), "x");
        File.WriteAllText(Path.Combine(real, "Engine", "b.bin"), "x");
        var linked = Path.Combine(_temp.FullName, "Linked");
        Junction(linked, real);
        var engine = Path.Combine(linked, "Engine");

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync(
            [Path.Combine(engine, "sub")], [Path.Combine(engine, "b.bin")], engine);

        result.Failed.Should().BeEmpty();
        Directory.Exists(Path.Combine(real, "Engine", "sub")).Should().BeFalse();
        File.Exists(Path.Combine(real, "Engine", "b.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task ARelativePath_IsRefused()
    {
        var fs = new Mock<IRemovalFileSystem>();

        var result = await new EngineRemover(_engine.Object, null, fileSystem: fs.Object).RemoveAsync(["."], ["b.bin"]);

        result.Failed.Should().HaveCount(2).And.OnlyContain(f => f.Reason.Contains("not a full path"));
        fs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ARelativeEngineRoot_IsIgnored_AndLinksAreStillChecked()
    {
        var engine = Folder("Engine");
        var library = Folder("Library");
        Junction(Path.Combine(engine, "models"), library);
        var viaLink = Path.Combine(engine, "models", "f.txt");

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([], [viaLink], "Engine");

        result.Failed.Should().ContainSingle().Which.Path.Should().Be(viaLink);
        File.Exists(Path.Combine(library, "f.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task AJunctionDirectoryWithATrailingSeparator_LosesTheLinkOnly()
    {
        var engine = Folder("Engine");
        var library = Folder("Library");
        var link = Path.Combine(engine, "custom_nodes");
        Junction(link, library);

        var result = await new EngineRemover(_engine.Object, null).RemoveAsync([link + Path.DirectorySeparatorChar], [], engine);

        result.Failed.Should().BeEmpty();
        Directory.Exists(link).Should().BeFalse();
        File.Exists(Path.Combine(library, "f.txt")).Should().BeTrue();
    }
}
