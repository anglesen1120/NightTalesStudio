namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// The idempotency steps of an autopilot run (02.8.3 plan §3.3 column <c>autopilot_runs.step</c> and §3.5's
/// "a step is executed only when <c>step</c> is strictly behind it"): the last completed step's name, stored as
/// the snake_case wire value, plus the ordering rule.
/// </summary>
/// <remarks>
/// <para>
/// The ladder is <c>idea</c> · <c>find_sources</c> · <c>fact_sheet</c> · <c>sources_required</c> · <c>story</c> ·
/// <c>story_gate</c> · <c>story2</c> · <c>render</c> · <c>video_gate</c> · <c>publication</c> · <c>consent</c> ·
/// <c>go_live</c>, with <see cref="Authorization"/> ahead of <c>idea</c> because plan §3.5's row
/// <c>planned → awaiting_authorization</c> carries the key <c>…:authorization</c>.
/// </para>
/// <para>
/// The column holds the bare step name, never the key and never a suffix: the plan's
/// <c>…:story_gate:{attempt}</c> is the same step <see cref="StoryGate"/> with
/// <see cref="AutopilotRun.Attempt"/> carried separately, so the fourth key of a run is built from the run and not
/// from this string. A test asserts the ladder; an unknown name throws rather than silently stalling a run.
/// </para>
/// <para>
/// <b>The one loop: <c>story</c>, <c>story_gate</c> and <c>story2</c>.</b> The first generation completes as
/// <c>story</c>, the gate verdict as <c>story_gate</c> (inside the same attempt), and a gate failure re-runs the
/// generation as <c>story2</c> — the one regenerate, ordered after the gate so a gate that already ran never
/// blocks it. The second gate would then be "behind" again, so <see cref="IsBehind"/> answers true for
/// <c>story2 → story_gate</c>: the gate is the only step allowed to repeat, and the attempt it repeats for is the
/// run's <see cref="AutopilotRun.Attempt"/>, which is why the plan's key ends in it.
/// </para>
/// </remarks>
public static class AutopilotRunStep
{
    /// <summary>The key <c>…:authorization</c>: the run waited for a valid authorization (plan §3.5 row 1).</summary>
    public const string Authorization = "authorization";

    /// <summary>The key <c>…:idea</c>: the slot's idea was picked and its episode created.</summary>
    public const string Idea = "idea";

    /// <summary>The key <c>…:find_sources</c>: a true-crime run's source search ran (plan §3.5, §7.3).</summary>
    public const string FindSources = "find_sources";

    /// <summary>The key <c>…:fact_sheet</c>: the fact sheet was drafted (plan §3.5 row <c>researching → waiting_owner</c>).</summary>
    public const string FactSheet = "fact_sheet";

    /// <summary>
    /// The key <c>…:sources_required</c>: the F33 branch asked the owner to paste the sources because no
    /// source-finder go exists (plan §3.5 row <c>idea_picked → waiting_owner</c>).
    /// </summary>
    public const string SourcesRequired = "sources_required";

    /// <summary>The key <c>…:story</c>: the first story generation of the run completed.</summary>
    public const string Story = "story";

    /// <summary>
    /// The key <c>…:story_gate:{attempt}</c>: the story gate ran. The step name is bare here; the attempt that
    /// makes the key is <see cref="AutopilotRun.Attempt"/>.
    /// </summary>
    public const string StoryGate = "story_gate";

    /// <summary>The key <c>…:story2</c>: the one regenerate of a story that failed its gate (plan §3.5, attempt 2).</summary>
    public const string Story2 = "story2";

    /// <summary>The key <c>…:render</c>: the run's single render was dispatched.</summary>
    public const string Render = "render";

    /// <summary>The key <c>…:video_gate</c>: the video gate ran.</summary>
    public const string VideoGate = "video_gate";

    /// <summary>The key <c>…:publication</c>: the run created its publication (plan §3.5 rows into <c>publication_created</c>).</summary>
    public const string Publication = "publication";

    /// <summary>The key <c>…:consent</c>: consent was written, so the publication is <c>scheduled</c> (06.2.2-T3).</summary>
    public const string Consent = "consent";

    /// <summary>The key <c>…:go_live</c>: the publication is live, or locked private in pre-audit mode.</summary>
    public const string GoLive = "go_live";

    /// <summary>
    /// Whether <paramref name="current"/> is strictly behind <paramref name="target"/>, which is the plan's rule for
    /// running <paramref name="target"/> at all: a step already completed is never executed a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No step so far (<see langword="null"/>) is behind every target; an equal step is not behind (it is done); a
    /// step further along the ladder is not behind either, so a redelivered effect handler is a no-op. The single
    /// exception is the regenerate loop's second gate, <c>story2 → story_gate</c>, which <paramref name="current"/>
    /// being <see cref="Story2"/> makes true (see the remarks on this class).
    /// </para>
    /// <para>
    /// Both names must be steps of the ladder: a value written outside <see cref="AutopilotRunStep"/> is a
    /// programming error and throws, because answering "not behind" would stall the run silently (02.8.3-T1 plan
    /// decision 13).
    /// </para>
    /// </remarks>
    /// <param name="current">The step the run's <c>step</c> column holds, or <see langword="null"/> when none ran.</param>
    /// <param name="target">The step whose execution is being decided.</param>
    /// <returns><see langword="true"/> when <paramref name="target"/> may run.</returns>
    /// <exception cref="ArgumentException">Either name is not a step of the ladder.</exception>
    public static bool IsBehind(string? current, string target)
    {
        var targetRank = Rank(target, nameof(target));

        if (current is null)
        {
            return true;
        }

        var currentRank = Rank(current, nameof(current));

        if (currentRank < targetRank)
        {
            return true;
        }

        // The regenerate loop: the gate re-runs on the story that attempt 2 generated.
        return current == Story2 && target == StoryGate;
    }

    /// <summary>Whether <paramref name="step"/> is a name of the ladder, i.e. one of this class's constants.</summary>
    /// <param name="step">The candidate name, typically a row's <c>step</c> column.</param>
    /// <returns><see langword="true"/> for the thirteen constants.</returns>
    public static bool IsKnown(string? step) => step switch
    {
        Authorization
            or Idea
            or FindSources
            or FactSheet
            or SourcesRequired
            or Story
            or StoryGate
            or Story2
            or Render
            or VideoGate
            or Publication
            or Consent
            or GoLive => true,
        _ => false,
    };

    /// <summary>
    /// The position of <paramref name="step"/> on the ladder, smallest first;
    /// <see cref="Authorization"/> is 0 and <see cref="GoLive"/> is 12.
    /// </summary>
    private static int Rank(string step, string parameterName) => step switch
    {
        Authorization => 0,
        Idea => 1,
        FindSources => 2,
        FactSheet => 3,
        SourcesRequired => 4,
        Story => 5,
        StoryGate => 6,
        Story2 => 7,
        Render => 8,
        VideoGate => 9,
        Publication => 10,
        Consent => 11,
        GoLive => 12,
        _ => throw new ArgumentException(
            $"'{step}' is not a step of the autopilot ladder (02.8.3 plan §3.5); use AutopilotRunStep.",
            parameterName),
    };
}
