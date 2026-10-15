namespace NightTales.Domain.Series.Bible;

/// <summary>
/// The post-processing rules of a bible proposal (02.6.3-T2, plan §7.1 step 5): the model's op list is never
/// trusted, so this pure type turns it into the ops a proposal may store and reports every candidate it dropped
/// with the closed reason code the job counts (<c>bible_proposal_ops_dropped_total{kind,reason}</c>).
/// </summary>
/// <remarks>
/// <para>
/// No I/O, no clock, no randomness. The content limits themselves live in <see cref="BibleContentValidator"/> and
/// the comparison key and truncation rule live in <see cref="BibleOpNormalizer"/>: this type reuses both rather
/// than restating either, so a drop can never disagree with what a manual PUT refuses.
/// </para>
/// <para>
/// It is deliberately a pre-filter and not the writer: <see cref="SeriesBible.ApplyProposal"/> stays the single
/// writer and re-decides every op against the content as it stands, so a dropped op here is an op that would
/// have added nothing anyway — except for the character rules, which are the reason the pre-filter exists: an op
/// holding a control, format, bidi or tag character (or <c>&lt;</c>/<c>&gt;</c>) must never reach the stored
/// proposal, because the PUT validator would refuse that text later (02.6.3-T1 hand-over, STATUS BIBLE-4).
/// </para>
/// <para>
/// The order of the checks is the plan's: kind, empty fields, duplicates, caps, plan §3.2 limits. An op that
/// breaks two rules is therefore reported under the earlier one, and the reasons stay deterministic.
/// </para>
/// </remarks>
public static class BibleProposalValidator
{
    /// <summary>The op kind is not one of the four the contract defines.</summary>
    public const string UnknownKind = "unknown_kind";

    /// <summary>A field the op kind needs is missing or blank, so the op would add nothing.</summary>
    public const string EmptyField = "empty_field";

    /// <summary>The op repeats the content it proposes to add, or an op accepted earlier in the same batch.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>The batch is already at its total or per-kind cap (<see cref="BibleProposalCaps"/>).</summary>
    public const string OverCap = "over_cap";

    /// <summary>The target array of the content is already at its plan §3.2 limit, so the op could not be applied.</summary>
    public const string OverLimit = "over_limit";

    /// <summary>The op holds <c>&lt;</c>/<c>&gt;</c> or a character that cannot be stored or displayed faithfully.</summary>
    public const string ForbiddenCharacter = "forbidden_character";

    /// <summary>The <see cref="BibleOpDrop.Kind"/> of a candidate whose kind is outside the closed list.</summary>
    /// <remarks>
    /// The model's own spelling is never echoed: a drop feeds a metric label, and unbounded model text in a label
    /// is both a cardinality leak and content in a log (D19).
    /// </remarks>
    public const string UnknownKindName = "unknown";

    /// <summary>
    /// The plan §3.2 numbers a proposal is checked against when the caller passes none: the field lengths an op is
    /// truncated to and the array counts an op may not push past. Every value is the record's own default, so a
    /// host whose configuration omits <c>Bible:Limits</c> still enforces the plan's numbers.
    /// </summary>
    private static readonly BibleLimits ContentLimits = new();

    /// <summary>The dedupe key prefix of a character op.</summary>
    private const string CharacterKey = "ch:";

    /// <summary>The dedupe key prefix of a place op.</summary>
    private const string PlaceKey = "pl:";

    /// <summary>The dedupe key prefix of a rule op.</summary>
    private const string RuleKey = "rl:";

    /// <summary>
    /// Turns the model's candidates into the ops one proposal may carry, dropping every candidate that is not one
    /// of the four kinds, that misses the field its kind needs, that repeats the content or an accepted op, that
    /// overruns a cap, or that would exceed a plan §3.2 limit.
    /// </summary>
    /// <remarks>
    /// The field lengths are <i>not</i> a drop reason (plan §4 U4: an accept never fails for length): a value
    /// beyond its field's limit is truncated on a word boundary, exactly as <see cref="SeriesBible.ApplyProposal"/>
    /// would truncate it, so the stored op is what the writer would have stored. At most one <c>add_summary</c> op
    /// survives the batch: the op carries no episode id, so a second summary op in one batch is a duplicate of the
    /// first by construction.
    /// </remarks>
    /// <param name="candidates">The model's raw ops, in the order it returned them.</param>
    /// <param name="current">The content as it stands, for the duplicate and limit rules.</param>
    /// <param name="caps">The batch caps of <c>Bible:Proposal</c>.</param>
    /// <returns>The accepted ops in model order, and one drop per candidate that became none.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public static BibleProposalValidationResult Validate(
        IReadOnlyList<BibleOpCandidate> candidates,
        SeriesBibleContent current,
        BibleProposalCaps caps) =>
        Validate(candidates, current, caps, ContentLimits);

    /// <summary>
    /// Turns the model's candidates into the ops one proposal may carry, checked against
    /// <paramref name="limits"/> instead of the plan's default numbers.
    /// </summary>
    /// <remarks>
    /// The host binds <c>Bible:Limits</c> to a <see cref="BibleLimits"/> instance and the writer applies it, so a
    /// caller that changes those numbers passes the same instance here: it is what keeps the pre-filter's
    /// truncation and limit rules identical to the writer's.
    /// </remarks>
    /// <param name="candidates">The model's raw ops, in the order it returned them.</param>
    /// <param name="current">The content as it stands, for the duplicate and limit rules.</param>
    /// <param name="caps">The batch caps of <c>Bible:Proposal</c>.</param>
    /// <param name="limits">The configured content limits.</param>
    /// <returns>The accepted ops in model order, and one drop per candidate that became none.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public static BibleProposalValidationResult Validate(
        IReadOnlyList<BibleOpCandidate> candidates,
        SeriesBibleContent current,
        BibleProposalCaps caps,
        BibleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(caps);
        ArgumentNullException.ThrowIfNull(limits);

        var ops = new List<BibleOp>(Math.Clamp(caps.MaxOps, 0, candidates.Count));
        var drops = new List<BibleOpDrop>();
        var characters = current.RecurringCharacters.Count;
        var places = current.Places.Count;
        var rules = current.Rules.Count;
        var characterOps = 0;
        var placeOps = 0;
        var ruleOps = 0;
        var summaryOps = 0;

        // Seeded with the content as it stands and then grown by every accepted op, so one lookup answers both
        // "already in the bible" and "already in this batch" — the plan's two dedupe rules for one batch.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Seed(seen, current);

        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            var (kind, name) = Classify(candidate.Kind);
            if (kind is null)
            {
                drops.Add(new BibleOpDrop(name, UnknownKind));
                continue;
            }

            var op = new BibleOp(kind.Value, candidate.Summary, candidate.Name, candidate.Description, candidate.Text, candidate.Reason);

            if (!HasRequiredField(op))
            {
                drops.Add(new BibleOpDrop(name, EmptyField));
                continue;
            }

            if (HasUnstorableCharacter(op))
            {
                drops.Add(new BibleOpDrop(name, ForbiddenCharacter));
                continue;
            }

            if (kind.Value == BibleOpKind.AddSummary)
            {
                if (summaryOps > 0 || ops.Count >= caps.MaxOps)
                {
                    drops.Add(new BibleOpDrop(name, summaryOps > 0 ? Duplicate : OverCap));
                    continue;
                }

                ops.Add(Truncate(op, limits));
                summaryOps++;
                continue;
            }

            var key = DedupeKey(op);
            if (key.Length > 0 && !seen.Add(key))
            {
                drops.Add(new BibleOpDrop(name, Duplicate));
                continue;
            }

            var capped = ops.Count >= caps.MaxOps
                || (kind.Value switch
                {
                    BibleOpKind.AddCharacter => characterOps >= caps.MaxCharacterOps,
                    BibleOpKind.AddPlace => placeOps >= caps.MaxPlaceOps,
                    _ => ruleOps >= caps.MaxRuleOps,
                });

            if (capped)
            {
                drops.Add(new BibleOpDrop(name, OverCap));
                continue;
            }

            var atLimit = kind.Value switch
            {
                BibleOpKind.AddCharacter => characters + characterOps >= limits.MaxCharacters,
                BibleOpKind.AddPlace => places + placeOps >= limits.MaxPlaces,
                _ => rules + ruleOps >= limits.MaxRules,
            };

            if (atLimit)
            {
                drops.Add(new BibleOpDrop(name, OverLimit));
                continue;
            }

            ops.Add(Truncate(op, limits));

            switch (kind.Value)
            {
                case BibleOpKind.AddCharacter:
                    characterOps++;
                    break;
                case BibleOpKind.AddPlace:
                    placeOps++;
                    break;
                default:
                    ruleOps++;
                    break;
            }
        }

        return new BibleProposalValidationResult(ops, drops);
    }

    /// <summary>
    /// Merges a new batch of ops into the pending proposal of a series (02.6.3-T2, plan §7.1 step 7,
    /// [DECISION] "merging, not replacing"): the stored ops first, in their order, then the new ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Character, place and rule ops are deduped on the <see cref="BibleOpNormalizer"/> key of the field that
    /// identifies them (<c>name</c> or <c>text</c>), against the stored ops and within the new batch, so a second
    /// approval can never stack the same character twice.
    /// </para>
    /// <para>
    /// Summary ops are never deduped here and are always kept, both of them: an op carries no episode id, so two
    /// summary ops may legitimately describe two different episodes, and replacing one with the other would
    /// silently lose an episode's summary. The one-summary-per-episode rule of
    /// <see cref="SeriesBible.ApplyProposal"/> is what protects the content when the ops are applied.
    /// </para>
    /// <para>
    /// The op order is preserved, because the accept applies the ops in exactly this order.
    /// </para>
    /// </remarks>
    /// <param name="existing">The pending proposal, when the series has one.</param>
    /// <param name="newOps">The validated ops of the new run, in model order.</param>
    /// <param name="maxOps">The total cap (<c>Bible:Proposal:MaxOps</c>); a non-positive cap refuses every op.</param>
    /// <returns>The merged ops, capped at <paramref name="maxOps"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="newOps"/> or an op in it is <see langword="null"/>.</exception>
    public static IReadOnlyList<BibleOp> Merge(BibleProposal? existing, IReadOnlyList<BibleOp> newOps, int maxOps)
    {
        ArgumentNullException.ThrowIfNull(newOps);

        if (maxOps <= 0)
        {
            return [];
        }

        var merged = new List<BibleOp>(Math.Min(maxOps, (existing?.Ops.Count ?? 0) + newOps.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Append(existing?.Ops);
        Append(newOps);

        return merged;

        void Append(IReadOnlyList<BibleOp>? ops)
        {
            if (ops is null)
            {
                return;
            }

            foreach (var op in ops)
            {
                if (merged.Count >= maxOps)
                {
                    return;
                }

                ArgumentNullException.ThrowIfNull(op);

                if (op.Kind == BibleOpKind.AddSummary)
                {
                    merged.Add(op);
                    continue;
                }

                var key = DedupeKey(op);
                if (key.Length > 0 && !seen.Add(key))
                {
                    continue;
                }

                merged.Add(op);
            }
        }
    }

    /// <summary>
    /// The stored proposal without the ops that may not be applied (02.6.3-T2: the accept path runs this before
    /// <see cref="SeriesBible.ApplyProposal"/>): an op whose kind needs a field it does not carry, or one holding
    /// <c>&lt;</c>/<c>&gt;</c> or a control, format, bidi or tag character, is dropped instead of stored.
    /// </summary>
    /// <remarks>
    /// The op list was written by an earlier build of this feature (or by the job before this validator existed),
    /// so the accept path cannot assume it was checked. A dropped op is counted as skipped by the caller — the
    /// append-only writer would have skipped it anyway, since it adds nothing — and ids and counts only are
    /// logged (D13/D19). The proposal's <see cref="BibleProposal.BasedOnVersion"/> and
    /// <see cref="BibleProposal.ProposedAt"/> are untouched: they describe the proposal, not the ops.
    /// </remarks>
    /// <param name="proposal">The stored proposal.</param>
    /// <returns>The same proposal without its unstorable ops, or the proposal itself when none had to go.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="proposal"/> or an op in it is <see langword="null"/>.</exception>
    public static BibleProposal SanitizeStored(BibleProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var kept = new List<BibleOp>(proposal.Ops.Count);
        foreach (var op in proposal.Ops)
        {
            ArgumentNullException.ThrowIfNull(op);

            if (HasRequiredField(op) && !HasUnstorableCharacter(op))
            {
                kept.Add(op);
            }
        }

        return kept.Count == proposal.Ops.Count ? proposal : proposal with { Ops = kept };
    }

    /// <summary>The closed kind of a raw candidate, and the snake_case name a drop reports.</summary>
    /// <param name="kind">The model's kind token, matched ordinally and case-sensitively like the strict schema's enum.</param>
    /// <returns>The kind and its wire name, or no kind and <see cref="UnknownKindName"/>.</returns>
    private static (BibleOpKind? Kind, string Name) Classify(string? kind) => kind switch
    {
        "add_summary" => (BibleOpKind.AddSummary, "add_summary"),
        "add_character" => (BibleOpKind.AddCharacter, "add_character"),
        "add_place" => (BibleOpKind.AddPlace, "add_place"),
        "add_rule" => (BibleOpKind.AddRule, "add_rule"),
        _ => (null, UnknownKindName),
    };

    /// <summary>The field a summary's title occupies (the op's <c>name</c>) carries an episode title, not a character name.</summary>
    /// <param name="op">The op to truncate.</param>
    /// <param name="limits">The configured limits.</param>
    /// <returns>The op with each value it will store cut to its own field's limit.</returns>
    private static BibleOp Truncate(BibleOp op, BibleLimits limits) => op.Kind switch
    {
        BibleOpKind.AddSummary => op with
        {
            Summary = BibleOpNormalizer.Truncate(op.Summary, limits.SummaryChars),
            Name = BibleOpNormalizer.Truncate(op.Name, limits.SummaryTitleChars),
        },
        BibleOpKind.AddCharacter or BibleOpKind.AddPlace => op with
        {
            Name = BibleOpNormalizer.Truncate(op.Name, limits.NameChars),
            Description = BibleOpNormalizer.Truncate(op.Description, limits.DescriptionChars),
        },
        BibleOpKind.AddRule => op with { Text = BibleOpNormalizer.Truncate(op.Text, limits.RuleChars) },
        _ => op,
    };

    /// <summary>
    /// Whether the op carries every field its kind needs. The writer skips an op that adds nothing, and the
    /// summary op is the one whose absence would cost the series an episode's summary — so a summary op without
    /// its text or its title is dropped here, which lets the job's deterministic summary fallback fire.
    /// </summary>
    /// <param name="op">The op to test.</param>
    /// <returns>Whether the op's own kind's fields are present and non-blank.</returns>
    private static bool HasRequiredField(BibleOp op) => op.Kind switch
    {
        BibleOpKind.AddSummary => !string.IsNullOrWhiteSpace(op.Summary) && !string.IsNullOrWhiteSpace(op.Name),
        BibleOpKind.AddCharacter or BibleOpKind.AddPlace => !string.IsNullOrWhiteSpace(op.Name),
        BibleOpKind.AddRule => !string.IsNullOrWhiteSpace(op.Text),
        _ => false,
    };

    /// <summary>
    /// Whether any value of the op that would be stored, or served to the panel, cannot be stored or displayed
    /// faithfully. The reason is included: T1's proposal DTO serves it, so it lands in the same <c>jsonb</c> column
    /// and the same response.
    /// </summary>
    /// <param name="op">The op to scan.</param>
    /// <returns>Whether the op carries <c>&lt;</c>/<c>&gt;</c> or a character <see cref="BibleContentValidator"/> refuses.</returns>
    private static bool HasUnstorableCharacter(BibleOp op) =>
        IsUnstorable(op.Summary)
        || IsUnstorable(op.Name)
        || IsUnstorable(op.Description)
        || IsUnstorable(op.Text)
        || IsUnstorable(op.Reason);

    /// <summary>
    /// Whether one value holds the D3/F36 metadata markup or a character that cannot be stored faithfully: the
    /// rule is <see cref="BibleContentValidator"/>'s own, reused so a proposal can never store text the next PUT
    /// would refuse (02.6.3-T1 hand-over).
    /// </summary>
    /// <param name="value">The value to scan.</param>
    /// <returns>Whether the value holds a forbidden character.</returns>
    private static bool IsUnstorable(string? value) =>
        value is not null
        && (value.Contains('<') || value.Contains('>') || BibleContentValidator.FindUnsafeCharacter(value) is not null);

    /// <summary>The dedupe key of an op, or the empty string when its identifying field normalises to nothing.</summary>
    /// <param name="op">The op to key.</param>
    /// <returns>The kind-prefixed normalised name or text.</returns>
    private static string DedupeKey(BibleOp op) => op.Kind switch
    {
        BibleOpKind.AddCharacter => CharacterKey + BibleOpNormalizer.Normalize(op.Name),
        BibleOpKind.AddPlace => PlaceKey + BibleOpNormalizer.Normalize(op.Name),
        BibleOpKind.AddRule => RuleKey + BibleOpNormalizer.Normalize(op.Text),
        _ => string.Empty,
    };

    /// <summary>Adds the identifying key of every entry the content already holds, so an op cannot re-add one.</summary>
    /// <param name="seen">The key set to grow.</param>
    /// <param name="current">The content as it stands.</param>
    private static void Seed(HashSet<string> seen, SeriesBibleContent current)
    {
        foreach (var character in current.RecurringCharacters)
        {
            Add(seen, CharacterKey, character.Name);
        }

        foreach (var place in current.Places)
        {
            Add(seen, PlaceKey, place.Name);
        }

        foreach (var rule in current.Rules)
        {
            Add(seen, RuleKey, rule.Text);
        }

        static void Add(HashSet<string> keys, string prefix, string? value)
        {
            var normalized = BibleOpNormalizer.Normalize(value);
            if (normalized.Length > 0)
            {
                keys.Add(prefix + normalized);
            }
        }
    }
}

/// <summary>
/// How many ops one proposal may carry (02.6.3-T2, plan §7.1 step 5): bound from
/// <c>Bible:Proposal:{MaxOps,MaxCharacterOps,MaxPlaceOps,MaxRuleOps}</c> (10/3/3/2) and registered as a singleton,
/// so a handler injects the record rather than the options type (the Application layer cannot reference
/// Infrastructure).
/// </summary>
/// <param name="MaxOps">The most ops one proposal may carry, of every kind together.</param>
/// <param name="MaxCharacterOps">The most <c>add_character</c> ops one proposal may carry.</param>
/// <param name="MaxPlaceOps">The most <c>add_place</c> ops one proposal may carry.</param>
/// <param name="MaxRuleOps">The most <c>add_rule</c> ops one proposal may carry.</param>
public sealed record BibleProposalCaps(
    int MaxOps = 10,
    int MaxCharacterOps = 3,
    int MaxPlaceOps = 3,
    int MaxRuleOps = 2);

/// <summary>
/// One candidate of a proposal that became no op (02.6.3-T2, plan §7.1 step 5), as the job's drop metric and its
/// log line report it: the kind and the closed reason, never the op's text.
/// </summary>
/// <param name="Kind">The snake_case kind of the candidate, or <see cref="BibleProposalValidator.UnknownKindName"/>.</param>
/// <param name="Reason">One of the reason constants of <see cref="BibleProposalValidator"/>.</param>
public sealed record BibleOpDrop(string Kind, string Reason);

/// <summary>
/// What one <see cref="BibleProposalValidator.Validate"/> accepted and what it dropped (02.6.3-T2, plan §7.1
/// step 5). Both lists are in the model's order.
/// </summary>
/// <param name="Ops">The ops that may be stored, already truncated to their fields' limits.</param>
/// <param name="Drops">One entry per candidate that became none.</param>
public sealed record BibleProposalValidationResult(IReadOnlyList<BibleOp> Ops, IReadOnlyList<BibleOpDrop> Drops);
