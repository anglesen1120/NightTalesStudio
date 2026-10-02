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
    /// <summary>SEC-6: a child's stdout beyond this is a failed sample, never buffered whole.</summary>
    internal const int MaxStdoutBytes = 1024 * 1024;

    /// <summary>
    /// SEC-1: the only variables a probe child sees. Everything else (ADAPTER_API_KEY(_NEXT),
    /// NODE_OPENAI_IMAGE_KEY, NTS_MPT_API_KEY, ...) stays in the adapter process.
    /// </summary>
    private static readonly string[] InheritedVariables = ["LANG", "LC_ALL", "HOME", "TMPDIR", "LD_LIBRARY_PATH"];

    internal const string ChildPath = "/usr/bin:/bin";

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

        info.Environment.Clear();
        info.Environment["PATH"] = ChildPath;
        foreach (var name in InheritedVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                info.Environment[name] = value;
            }
        }

        using var process = Process.Start(info) ?? throw new Win32Exception("The process did not start.");
        process.StandardInput.Close();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        // stderr is drained and dropped (S7: a script's text never reaches a log or a body).
        var stdout = ReadCappedAsync(process.StandardOutput.BaseStream, limit.Token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, limit.Token);
        try
        {
            // The cap is enforced while reading, so an over-cap child is killed by the finally below.
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, Encoding.UTF8.GetString(await stdout.ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The process outlived its budget.");
        }
        finally
        {
            // Timeout, caller cancellation and an over-cap stream all leave the child running: kill its tree.
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the check and the kill.
                }
            }
        }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxStdoutBytes)
            {
                throw new IOException("The process output exceeded its cap.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
