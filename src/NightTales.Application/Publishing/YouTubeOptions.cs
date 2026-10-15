namespace NightTales.Application.Publishing;

/// <summary>
/// The <c>YouTube</c> configuration section (06.3.1-T1). Only the quota key exists here; the other keys of the
/// YouTube section arrive with their own tasks. Bound through <c>IOptionsMonitor&lt;YouTubeOptions&gt;</c>.
/// </summary>
public sealed class YouTubeOptions
{
    /// <summary>The configuration section the composition root binds.</summary>
    public const string SectionName = "YouTube";

    /// <summary>The upload quota settings.</summary>
    public YouTubeQuotaOptions Quota { get; set; } = new();
}

/// <summary>The <c>YouTube:Quota</c> settings.</summary>
public sealed class YouTubeQuotaOptions
{
    /// <summary>Units the platform grants per Pacific day. The day row seeds its limit from this value once.</summary>
    public int UnitsPerDay { get; set; } = 100;
}
