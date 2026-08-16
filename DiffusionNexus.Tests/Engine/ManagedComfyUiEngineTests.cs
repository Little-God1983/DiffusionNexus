using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class ManagedComfyUiEngineTests
{
    [Fact]
    public void AllocateFreePort_NeverReturns8188()
    {
        for (var i = 0; i < 20; i++)
        {
            var port = ManagedComfyUiEngine.AllocateFreePort();
            port.Should().NotBe(8188, "a user's own ComfyUI owns the default port");
            port.Should().BeInRange(1024, 65535);
        }
    }

    [Fact]
    public void BuildArguments_BindsLoopbackOnlyAndDisablesTheBrowser()
    {
        var args = ManagedComfyUiEngine.BuildArguments(@"C:\Engine\ComfyUI\main.py", 51234);

        args.Should().Contain("--listen 127.0.0.1");
        args.Should().Contain("--port 51234");
        args.Should().Contain("--disable-auto-launch");
        args.Should().Contain("\"C:\\Engine\\ComfyUI\\main.py\"",
            "the script path must be quoted so folders with spaces work");
    }

    [Fact]
    public void ResolveVenvPython_FindsTheEngineVenvInterpreter()
    {
        var root = Path.Combine(Path.GetTempPath(), "dn-engine-" + Guid.NewGuid());
        var scripts = Path.Combine(root, "venv", "Scripts");
        Directory.CreateDirectory(scripts);
        try
        {
            ManagedComfyUiEngine.ResolveVenvPython(root).Should().BeNull("no interpreter exists yet");

            File.WriteAllText(Path.Combine(scripts, "python.exe"), "");
            ManagedComfyUiEngine.ResolveVenvPython(root).Should().Be(Path.Combine(scripts, "python.exe"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureRunning_FailsClearlyWhenTheEngineIsNotInstalled()
    {
        await using var engine = new ManagedComfyUiEngine(unifiedLogger: null);

        var result = await engine.EnsureRunningAsync(
            Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid()),
            CancellationToken.None);

        result.IsRunning.Should().BeFalse();
        result.BaseUrl.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }
}
