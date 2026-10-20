namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The real-world detector's knobs (02.8.2-T2, <c>Autopilot:Gates:RealWorld</c>): whether a date pattern is only a
/// signal together with an incident word, and the incident words themselves. Data, not code — the list is
/// configuration (plan §3.4) and T3 tunes it on the first ten episodes; the detector never hard-codes a word.
/// </summary>
/// <param name="RequireIncidentCoOccurrence">
/// True — the default, and the main false-positive control (task §"Incident words"). A <c>DatedIncident</c> hit
/// then needs a date pattern <em>and</em> an incident word in the same segment. A bare year is never a hit, in
/// either mode: with co-occurrence off only a specific date (day/month/year, or a month and a year) counts, so
/// "năm 2020" cannot fail an ordinary fictional story that mentions a year.
/// </param>
/// <param name="IncidentWords">
/// The vi and en incident words. Blank entries are ignored and each word is trimmed before matching, so an
/// appsettings list with stray whitespace behaves like a clean one; the words match diacritic- and
/// case-insensitively, like the framing phrases.
/// </param>
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
