using DiffusionNexus.UI.Services.SpellCheck;

namespace DiffusionNexus.Tests.Services;

public class AutoCompleteDeferralTests
{
    [Fact]
    public async Task DeferredCtor_DoesNotStartLoading_UntilSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Nonexistent directory: LoadFromDictionary returns immediately once it runs,
        // so LoadCompleted completing == the load ran.
        var svc = new AutoCompleteService(signal.Task,
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

        await Task.Delay(150);
        Assert.False(svc.LoadCompleted.IsCompleted);

        signal.SetResult();
        await svc.LoadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(svc.LoadCompleted.IsCompletedSuccessfully);
    }
}
