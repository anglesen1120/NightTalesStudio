namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The content-policy settings of the idea step (02.8.1-T2, task Design "Content policy"):
/// <c>Autopilot:Ideas:Policy</c>. The keyword lists are data, not code (task Steps item 5), so both of them
/// ship empty here and the endpoint's configuration carries the vi and en forms.
/// </summary>
public sealed class IdeaPolicyOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Autopilot:Ideas:Policy";

    /// <summary>The one matching mode the banned-theme check implements, and the value the validator requires.</summary>
    public const string WordMatchMode = "word";

    /// <summary>
    /// Whether every candidate that survives the banned-theme check is asked about at the OpenAI moderation
    /// endpoint (D12). The shipped default is <see langword="true"/>: the pre-check is not the thing to
    /// economise on. Switching it off skips the calls but leaves the banned-theme check running.
    /// </summary>
    public bool ModerationEnabled { get; set; } = true;

    /// <summary>
    /// Whether an unavailable moderator rejects the batch (fail closed) or clears it. The shipped default is
    /// <see langword="false"/> — the endpoint is free and usually up, and the counter
    /// <c>moderation_unavailable_total</c> is what makes its outages visible — so a deployment that must not
    /// buy ideas nothing checked turns this on.
    /// </summary>
    public bool FailClosed { get; set; }

    /// <summary>
    /// The banned-theme matching mode; only <see cref="WordMatchMode"/> is implemented, and the option exists so
    /// the validator can refuse a deployment that asked for a mode the feature does not have.
    /// </summary>
    public string BannedThemeMatchMode { get; set; } = WordMatchMode;

    /// <summary>
    /// The crime nouns that arm the dated-incident check of a true-crime series (F31): a year within the
    /// configured distance of one of them reads as a real incident. The vi and en forms come from appsettings.
    /// </summary>
    public IReadOnlyList<string> CrimeNouns { get; set; } = [];

    /// <summary>
    /// The real-world framing phrases of a true-crime series (F31): a candidate carrying one of them requires
    /// the owner's research before it is written. The vi and en forms come from appsettings.
    /// </summary>
    public IReadOnlyList<string> FramingLexicon { get; set; } = [];
}
