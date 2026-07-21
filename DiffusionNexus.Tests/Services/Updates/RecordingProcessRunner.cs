using System.Collections.Concurrent;
using DiffusionNexus.Service.Services;

namespace DiffusionNexus.Tests.Services.Updates;

/// <summary>
/// A single recorded call to <see cref="IProcessRunner.RunAsync"/>.
/// </summary>
internal sealed record ProcessInvocation(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment);

/// <summary>
/// Test double for <see cref="IProcessRunner"/> that records every invocation (in order,
/// thread-safely) and returns canned <see cref="ProcessResult"/>s. Optionally blocks the
/// first <c>git fetch --all</c> on a gate so the in-flight-collapse race can be exercised
/// deterministically: while the gate is held, the first check is parked and every
/// concurrent same-key caller joins the same in-flight task instead of launching again.
/// </summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly Func<string, string, string, ProcessResult> _responder;
    private readonly TaskCompletionSource? _fetchGate;
    private readonly ConcurrentQueue<ProcessInvocation> _invocations = new();

    public RecordingProcessRunner(
        Func<string, string, string, ProcessResult>? responder = null,
        TaskCompletionSource? fetchGate = null)
    {
        _responder = responder ?? ((_, _, _) => new ProcessResult(0, string.Empty, string.Empty));
        _fetchGate = fetchGate;
    }

    /// <summary>All invocations in the order they were started.</summary>
    public IReadOnlyList<ProcessInvocation> Invocations => _invocations.ToList();

    /// <summary>Total number of process launches recorded.</summary>
    public int TotalInvocations => _invocations.Count;

    /// <summary>Number of launches whose arguments exactly match <paramref name="arguments"/>.</summary>
    public int CountByArguments(string arguments)
        => _invocations.Count(i => i.Arguments == arguments);

    /// <summary>Index of the first invocation matching <paramref name="predicate"/>, or -1.</summary>
    public int IndexOf(Func<ProcessInvocation, bool> predicate)
    {
        var list = Invocations;
        for (var i = 0; i < list.Count; i++)
            if (predicate(list[i]))
                return i;
        return -1;
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        _invocations.Enqueue(new ProcessInvocation(fileName, arguments, workingDirectory, environment));

        if (_fetchGate is not null && arguments == "fetch --all")
            await _fetchGate.Task.ConfigureAwait(false);

        return _responder(fileName, arguments, workingDirectory);
    }
}
