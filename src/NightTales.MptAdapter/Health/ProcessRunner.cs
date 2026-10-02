using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NightTales.MptAdapter.Health;

/// <summary>What a finished child process left behind; only the exit code and stdout feed the samplers.</summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput);

/// <summary>
/// The one way the samplers start a child process (<c>nvidia-smi</c>, <c>ffmpeg</c>, the assets scripts): a file
/// and an argument list, never a shell string. A missing binary surfaces as <see cref="Win32Exception"/> so a
/// sampler can turn it into its failure reason; tests substitute a counting fake to prove the caches.
/// </summary>
internal interface IProcessRunner
{
    /// <exception cref="Win32Exception">The file does not exist or is not executable.</exception>
    /// <exception cref="TimeoutException">The process outlived <paramref name="timeout"/> and was killed.</exception>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new Win32Exception("The process did not start.");
        process.StandardInput.Close();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        // stderr is drained and dropped (S7: a script's text never reaches a log or a body).
        var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
        var stderr = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The process outlived its budget.");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false));
    }
}
