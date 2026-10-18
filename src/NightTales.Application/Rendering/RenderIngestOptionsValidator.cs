namespace NightTales.Application.Rendering;

/// <summary>
/// Checks <see cref="RenderIngestOptions"/> so a bad <c>Render:Ingest</c>/<c>Assets</c> value stops the Worker's
/// start-up instead of the first ingest (the <c>RenderOptionsValidator</c> precedent), including the one check of
/// task step 2 that cannot be made from the values alone: the temp directory must exist and be writable.
/// </summary>
/// <remarks>
/// <para>
/// The retention window is an interval (a zero one would let the sweep delete an asset in the same instant it was
/// written) and the directory is where rendered video lands, so both are refused rather than clamped. The
/// directory is <em>created</em> when it is missing — the job would otherwise fail its first download on a fresh
/// host — and then probed with a tiny file, because "the directory exists" says nothing about the account the
/// Worker runs as (a root-owned 700 spool reads as present and is unwritable).
/// </para>
/// <para>
/// This is the only validator in the Application layer that touches the filesystem, deliberately: task step 2
/// asks for the check at start-up, and a host that cannot spool an output has nothing useful to do. A failure is
/// reported as a problem message, and the composition root turns it into a refused boot.
/// </para>
/// </remarks>
public static class RenderIngestOptionsValidator
{
    /// <summary>The longest accepted retention window: ten years.</summary>
    public const int MaxUnapprovedRetentionDays = 3_650;

    /// <summary>The probe file the writability check creates and removes.</summary>
    private const string ProbeFileName = ".nts-ingest-probe";

    /// <summary>
    /// Validates the bound options: a non-blank, creatable, writable temp directory and a retention window of at
    /// least one day and at most ten years.
    /// </summary>
    /// <param name="options">The bound options.</param>
    /// <returns>One message per problem; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(RenderIngestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (options.UnapprovedRetentionDays is < 1 or > MaxUnapprovedRetentionDays)
        {
            problems.Add(
                $"{nameof(options.UnapprovedRetentionDays)} must be between 1 and {MaxUnapprovedRetentionDays}.");
        }

        if (string.IsNullOrWhiteSpace(options.TempDirectory))
        {
            problems.Add($"{nameof(options.TempDirectory)} must name the directory the ingest spools into.");
        }
        else if (Probe(options.TempDirectory) is { } problem)
        {
            problems.Add($"{nameof(options.TempDirectory)}: {problem}");
        }

        return problems;
    }

    /// <summary>
    /// Ensures <paramref name="directory"/> exists and can be written to, and answers why it cannot when so.
    /// </summary>
    /// <param name="directory">The configured spool directory.</param>
    /// <returns>The reason it is unusable, or <see langword="null"/> when it is ready.</returns>
    private static string? Probe(string directory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                // Owner-only, like the blob spool: the directory holds rendered video of a paid attempt.
                Directory.CreateDirectory(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var probe = Path.Combine(directory, ProbeFileName);
            using (var stream = new FileStream(
                probe,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                }))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);

            return null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            // The type name only: the path is configuration, and a message may carry it.
            return $"the directory is not writable ({exception.GetType().Name}).";
        }
    }
}
