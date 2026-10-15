namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// The <c>Bible:Proposal</c> section as the Application layer reads it (02.6.3-T2 plan §7.1, §"Config keys"):
/// the kill switch and the op caps of one bible-proposal run.
/// </summary>
/// <remarks>
/// <para>
/// The section is bound a second time here on purpose, exactly as <c>FactSheetExtractionOptions</c> binds
/// <c>Facts</c>: the layer that decides whether to queue a proposal — the <c>StoryApproved</c> handler and the
/// proposal job, both Application code — cannot reference the Infrastructure options class
/// (<c>NightTales.Infrastructure.Bible.BibleOptions</c>) that binds the same keys for the adapter. Both are
/// validated on start by their own host, they read the same keys, and every member's default is the plan's value,
/// so a host whose section is missing still behaves as the plan says.
/// </para>
/// <para>
/// <see cref="Enabled"/> is the rollback switch of the feature (plan §11, 09.10.3): with it off an approval
/// queues no run at all — no row, no paid call — and the handler reads it per delivery through
/// <c>IBibleProposalSwitch</c>, the port the host implements over this section, so turning it off needs no
/// restart.
/// </para>
/// <para>
/// The members are mutable (<c>get; set;</c>) because the options binder fills them; nothing else writes them.
/// </para>
/// </remarks>
public sealed class BibleProposalJobOptions
{
    /// <summary>The configuration section name (<c>Bible:Proposal</c>).</summary>
    public const string SectionName = "Bible:Proposal";

    /// <summary>The default of <see cref="MaxOps"/>: the plan's total op cap.</summary>
    public const int DefaultMaxOps = 10;

    /// <summary>The default of <see cref="MaxCharacterOps"/>: the plan's <c>add_character</c> cap.</summary>
    public const int DefaultMaxCharacterOps = 3;

    /// <summary>The default of <see cref="MaxPlaceOps"/>: the plan's <c>add_place</c> cap.</summary>
    public const int DefaultMaxPlaceOps = 3;

    /// <summary>The default of <see cref="MaxRuleOps"/>: the plan's <c>add_rule</c> cap.</summary>
    public const int DefaultMaxRuleOps = 2;

    /// <summary>Whether approving a story queues a proposal run at all (the feature's kill switch).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most ops one proposal keeps, all kinds together.</summary>
    public int MaxOps { get; set; } = DefaultMaxOps;

    /// <summary>The most <c>add_character</c> ops one proposal keeps.</summary>
    public int MaxCharacterOps { get; set; } = DefaultMaxCharacterOps;

    /// <summary>The most <c>add_place</c> ops one proposal keeps.</summary>
    public int MaxPlaceOps { get; set; } = DefaultMaxPlaceOps;

    /// <summary>The most <c>add_rule</c> ops one proposal keeps.</summary>
    public int MaxRuleOps { get; set; } = DefaultMaxRuleOps;
}
