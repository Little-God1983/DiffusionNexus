using System.Diagnostics;
using System.Text;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Production <see cref="IProcessRunner"/> that launches real OS processes. This is the
/// single home for the process machinery that previously lived (duplicated) inside both
/// <see cref="ComfyUIUpdateService"/> and <see cref="AIToolkitUpdateService"/>:
/// stdout/stderr redirection, live per-line streaming, and — critically — killing the
/// whole process tree on cancellation so an aborted git/pip run doesn't keep child
/// processes alive and corrupt the installation.
/// </summary>
internal sealed class DefaultProcessRunner : IProcessRunner
{
    private static readonly ILogger Logger = Log.ForContext<DefaultProcessRunner>();

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                    psi.EnvironmentVariables.Remove(key);
                else
                    psi.EnvironmentVariables[key] = value;
            }
        }

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException($"Failed to start {fileName}");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stdout.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stderr.AppendLine(e.Data);
                // git and pip both write progress information to stderr — surface it too.
                onOutputLine?.Invoke(e.Data);
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Kill the whole tree so child processes (e.g. pip's spawned setup.py
                // builds) don't keep running and corrupt the installation.
                TryKillProcessTree(process, fileName, arguments, workingDirectory);
                throw;
            }

            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            process?.Dispose();
        }
    }

    // TODO: Linux Implementation - Process.Kill(entireProcessTree) works on Linux but
    // sends SIGKILL; a graceful SIGTERM-then-SIGKILL sequence may be preferable there.
    private static void TryKillProcessTree(Process process, string fileName, string arguments, string workingDir)
    {
        try
        {
            if (!process.HasExited)
            {
                Logger.Warning("Killing process tree for {Exe} {Args} in {Dir} due to cancellation",
                    fileName, arguments, workingDir);
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception killEx)
        {
            Logger.Error(killEx, "Failed to kill {Exe} on cancellation", fileName);
        }
    }
}
