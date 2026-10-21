namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The <c>Autopilot:Ideas</c> section as the Application layer reads it (02.8.1-T1 plan §3.2, §10 item 4):
/// how many ideas one run asks for and the hard ceiling a model answer may be cut to.
/// </summary>
/// <remarks>
/// <para>
/// The section is bound a second time here on purpose, exactly as <c>BibleProposalJobOptions</c> binds
/// <c>Bible:Proposal</c>: the layer that clamps the count — the planner — cannot reference the Infrastructure
/// options class (<c>NightTales.Infrastructure.Autopilot.IdeaOptions</c>) that binds the same keys for the
/// adapter and the context provider. Both are validated on start by their own host, they read the same keys, and
/// every member's default is the plan's value, so a host whose section is missing still behaves as the plan says.
/// </para>
/// <para>
/// The members are mutable (<c>get; set;</c>) because the options binder fills them; nothing else writes them.
/// </para>
/// </remarks>
public sealed class IdeaPlannerOptions
{
    /// <summary>The configuration section name (<c>Autopilot:Ideas</c>).</summary>
    public const string SectionName = "Autopilot:Ideas";

    /// <summary>The default of <see cref="Count"/>: the plan's batch size.</summary>
    public const int DefaultCount = 5;

    /// <summary>The default of <see cref="MaxCount"/>: the plan's ceiling, and the strict schema's <c>maxItems</c>.</summary>
    public const int DefaultMaxCount = 10;

    /// <summary>
    /// How many ideas a run asks for when the context reports no usable count. The context provider reads the
    /// same key from its own copy of the section; this is the planner's fallback, so a mis-bound provider still
    /// buys the plan's batch instead of one idea.
    /// </summary>
    public int Count { get; set; } = DefaultCount;

    /// <summary>
    /// The most ideas one run may keep: candidates beyond this index are dropped before validation (the plan's
    /// failure table), and it is the ceiling the request's count is clamped to.
    /// </summary>
    public int MaxCount { get; set; } = DefaultMaxCount;
}
