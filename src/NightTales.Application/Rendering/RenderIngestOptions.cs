namespace NightTales.Application.Rendering;

/// <summary>
/// The ingest's own configuration (09.6.3-T2 steps 2-3 and 7; BD7, WT45/WT56): where the job streams an output
/// before it verifies it, and how long an <c>assets</c> row of an unapproved episode is kept before 09.7.3-T1's
/// sweep may delete it.
/// </summary>
/// <remarks>
/// <para>
/// Two keys of two sections, because they are the two values the ingest owns and no other component reads:
/// <c>Render:Ingest:TempDirectory</c> (this job's private spool) and <c>Assets:UnapprovedRetentionDays</c> (the
/// abandoned-render window WT56 fixes; the approve path of 09.7.1-T1 re-stamps the same rows with
/// <c>Assets:RetentionDays</c>). The byte caps are <em>not</em> here: <c>Render:MaxVideoBytes</c> and
/// <c>Render:MaxSubtitleBytes</c> belong to <see cref="RenderOptions"/> and its Infrastructure twin, and are read
/// from there rather than declared a second time.
/// </para>
/// <para>
/// The section is bound and validated in the <b>Worker only</b> (<c>ValidateOnStart</c>), like every other
/// options class the Worker's jobs take: the Application layer holds the values and the checks
/// (<see cref="RenderIngestOptionsValidator"/>), the composition root owns the binding, because Application may
/// not reference the Options package. The bound value is registered as itself, so the ingest takes
/// <see cref="RenderIngestOptions"/> directly.
/// </para>
/// <para>
/// Every value has an in-code default, so a host that carries no <c>Render:Ingest</c> key still runs (the keys
/// are what an operator tunes, not what makes the job start), and an impossible one fails the boot.
/// </para>
/// </remarks>
public sealed class RenderIngestOptions
{
    /// <summary>The configuration section of the ingest's own keys (BD7).</summary>
    public const string SectionName = "Render:Ingest";

    /// <summary>The section the retention window is bound from (09.7.3-T1's own section, WT56).</summary>
    public const string RetentionSectionName = "Assets";

    /// <summary>
    /// The default retention window of an unapproved asset, in days (WT56, task §Notes: 30).
    /// </summary>
    public const int DefaultUnapprovedRetentionDays = 30;

    /// <summary>
    /// The private directory the ingest streams an output into before it verifies it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under <c>$NTS_HOME/tmp/render-ingest</c> when <c>NTS_HOME</c> is set, and under the platform's own temp
    /// directory — <c>/tmp/nts-render-ingest</c> on Linux — otherwise. On many Linux hosts <c>/tmp</c> is a tmpfs
    /// (RAM-backed) shared by every local account, so production SHOULD set <c>NTS_HOME</c> or this key to a
    /// service-owned directory on disk rather than rely on the fallback. It is never inside the repository: the
    /// files hold rendered video, and step 3 rules out a working-tree path.
    /// </para>
    /// <para>
    /// The value MUST be an absolute path: <see cref="RenderIngestOptionsValidator"/> refuses a relative one,
    /// which would otherwise spool relative to the Worker's working directory. The job creates the directory
    /// owner-only when it is missing (mode 0700 on POSIX), exactly as the blob spool does.
    /// </para>
    /// </remarks>
    public string TempDirectory { get; set; } = DefaultTempDirectory();

    /// <summary>
    /// How many days an <c>assets</c> row of a render whose episode is not approved is kept (WT56, default 30).
    /// </summary>
    public int UnapprovedRetentionDays { get; set; } = DefaultUnapprovedRetentionDays;

    /// <summary>The retention stamp an <c>assets</c> row written by the ingest carries.</summary>
    public TimeSpan UnapprovedRetention => TimeSpan.FromDays(UnapprovedRetentionDays);

    /// <summary>The temp directory a host without <c>Render:Ingest:TempDirectory</c> uses.</summary>
    /// <returns>
    /// <c>$NTS_HOME/tmp/render-ingest</c> when <c>NTS_HOME</c> is set, else
    /// <c>&lt;platform temp&gt;/nts-render-ingest</c>. Absolute only when <c>NTS_HOME</c> itself is absolute: a
    /// relative <c>NTS_HOME</c> yields a relative path, which <see cref="RenderIngestOptionsValidator"/> refuses
    /// at boot.
    /// </returns>
    public static string DefaultTempDirectory()
    {
        var home = Environment.GetEnvironmentVariable("NTS_HOME");

        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Path.GetTempPath(), "nts-render-ingest")
            : Path.Combine(home, "tmp", "render-ingest");
    }
}
