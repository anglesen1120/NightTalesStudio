namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The real-world detector's knobs (02.8.2-T2, <c>Autopilot:Gates:RealWorld</c>): whether a date pattern is only a
/// signal together with an incident word, and the incident words themselves. Data, not code — the list is
/// configuration (plan §3.4) and T3 tunes it on the first ten episodes; the detector never hard-codes a word.
/// </summary>
/// <param name="RequireIncidentCoOccurrence">
/// True — the default, and the main false-positive control (task §"Incident words"). A <c>DatedIncident</c> hit
/// then needs a specific date <em>and</em> an incident word in the same segment; with it off a specific date alone
/// is a hit. What exactly counts as a specific date is in <see cref="RealWorldOptions"/>'s remarks, and the
/// patterns in <see cref="RealWorldSignalDetector"/> are the only definition of it.
/// </param>
/// <param name="IncidentWords">
/// The vi and en incident words. Blank entries are ignored and each word is trimmed before matching, so an
/// appsettings list with stray whitespace behaves like a clean one; the words match diacritic- and
/// case-insensitively, like the framing phrases, with invisible code points dropped and any whitespace run counted
/// as one space.
/// </param>
/// <remarks>
/// <para>
/// A specific date, spelled out so the detector and this documentation cannot drift apart:
/// </para>
/// <list type="bullet">
/// <item><description>
/// vi: <c>ngày 12/3/2019</c> or <c>12-3-2019</c> (<c>ngày</c> optional), <c>ngày 12 tháng 3</c>,
/// <c>ngày 12 tháng 3 năm 2019</c>, <c>tháng 3 năm 2019</c> and <c>tháng 3/2019</c>;
/// </description></item>
/// <item><description>
/// en: <c>March 12, 2019</c>, <c>March, 2019</c>, <c>March 2019</c>, <c>Mar 2019</c> (a month and a year, the day
/// optional on either side), <c>12 March 2019</c> and <c>12/3/2019</c> or <c>12-3-2019</c>.
/// </description></item>
/// </list>
/// <para>
/// Two shapes are deliberately not dates: a bare year (<c>năm 2020</c>, <c>in 1999</c>) and a bare month/year
/// number (<c>3/2019</c>), which is as likely a chapter count as a date. With co-occurrence off, only a specific
/// date counts, so <c>năm 2020</c> cannot fail an ordinary fictional story that mentions a year.
/// </para>
/// <para>
/// Accepted fail-closed noise: a month name that is also an ordinary word followed by a year (<c>may 2019</c>,
/// <c>march 2019</c>) is read as a date, so such a segment that also holds an incident word is a hit — a missed
/// real-world claim costs more than an owner review.
/// </para>
/// </remarks>
public sealed record RealWorldOptions(bool RequireIncidentCoOccurrence, IReadOnlyList<string> IncidentWords)
{
    /// <summary>The task file's incident words, the twelve vi entries then the eleven en ones (§"Incident words").</summary>
    private static readonly string[] DefaultIncidentWords =
    [
        "vụ án",
        "vụ thảm sát",
        "án mạng",
        "giết người",
        "tai nạn",
        "vụ nổ",
        "bắt giữ",
        "kết án",
        "tù chung thân",
        "tử hình",
        "mất tích",
        "tự tử",
        "murder case",
        "homicide",
        "massacre",
        "shooting",
        "arrested",
        "convicted",
        "sentenced",
        "life sentence",
        "death penalty",
        "went missing",
        "suicide",
    ];

    /// <summary>
    /// The plan's defaults: co-occurrence required, with the twelve vi and eleven en incident words of the task
    /// file, in that order.
    /// </summary>
    public static RealWorldOptions Defaults { get; } = new(true, DefaultIncidentWords);
}
