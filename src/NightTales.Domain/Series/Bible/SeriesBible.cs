using System.Globalization;
using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Bible;

/// <summary>
/// The series bible (02.6.3 plan §3.3, 00 §6 <c>series_bibles</c>): the document a series remembers — narrator,
/// recurring characters and places, standing rules and the summaries of earlier approved episodes — plus the
/// proposal a background job may have staged for it.
/// </summary>
/// <remarks>
/// <para>
/// A series owns exactly one row, created with the series itself (<see cref="CreateDefault"/>, called by
/// <c>CreateSeriesHandler</c> inside the series' own transaction) and replaced in place by
/// <c>PUT /series/{id}/bible</c>. <see cref="Version"/> is the concurrency token and the ETag source
/// (<c>"sb-&lt;version&gt;"</c>, D9) — never <c>xmin</c>, and no second concurrency mechanism.
/// </para>
/// <para>
/// A manual save keeps a pending proposal: the proposal is an <b>append-only</b> op list (F27), so an edit can
/// never invalidate it. Accepting (<see cref="ApplyProposal"/>) applies the ops onto the content <em>as it is
/// now</em>, bumps the version and clears the proposal.
/// </para>
/// <para>
/// Every mutation validates before it writes: <see cref="ReplaceContent"/> re-checks the content it is given,
/// and <see cref="ApplyProposal"/> only adds entries that keep the content valid (an op is skipped, never
/// stored broken), so a stored bible always satisfies the plan §3.2 rules.
/// </para>
/// </remarks>
public sealed class SeriesBible : Entity, IUserOwned, IAuditable
{
    /// <summary>The first <c>version</c> of every bible (<c>default 1</c>, plan §3.3).</summary>
    public const int InitialVersion = 1;

    private SeriesBible()
    {
    }

    /// <summary>The owner of the series; the tenant filter and the ownership rules key on it.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The series this bible belongs to; unique, so a series has at most one row.</summary>
    public Guid SeriesId { get; private set; }

    /// <summary>The 1-based content version: bumped by a save and by an accepted proposal (D9).</summary>
    public int Version { get; private set; }

    /// <summary>The stored <c>bible.v1</c> document.</summary>
    public SeriesBibleContent Content { get; private set; } = SeriesBibleContent.Empty;

    /// <summary>The pending proposal, or <see langword="null"/> when none is staged.</summary>
    public BibleProposal? ProposedContent { get; private set; }

    /// <summary>The episode whose approval created (or last extended) <see cref="ProposedContent"/>.</summary>
    public Guid? ProposedFromEpisodeId { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates the empty bible of a new series (version <see cref="InitialVersion"/>, no proposal). Called by
    /// <c>CreateSeriesHandler</c> in the same transaction as the series insert, so a series without a bible row
    /// is data drift, not a state (plan §4 U1).
    /// </summary>
    /// <param name="seriesId">The series the bible belongs to.</param>
    /// <param name="userId">The owner of the series; also the owner of the bible.</param>
    /// <param name="clock">The clock reading of the insert.</param>
    /// <returns>The new bible, holding <see cref="SeriesBibleContent.Empty"/>.</returns>
    /// <exception cref="ArgumentException">The series or the owner is <see cref="Guid.Empty"/>.</exception>
    public static SeriesBible CreateDefault(Guid seriesId, Guid userId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (seriesId == Guid.Empty)
        {
            throw new ArgumentException("A series bible needs its series.", nameof(seriesId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A series bible needs its owner.", nameof(userId));
        }

        return new SeriesBible
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            SeriesId = seriesId,
            Version = InitialVersion,
            Content = SeriesBibleContent.Empty,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
    }

    /// <summary>
    /// Replaces the content of a manual save (plan §4 U2): bumps <see cref="Version"/> and
    /// <see cref="UpdatedAt"/>, and leaves a pending proposal exactly as it is — its ops stay append-only, so a
    /// save cannot invalidate them.
    /// </summary>
    /// <param name="content">The new content; the caller has already checked it against the approved episodes.</param>
    /// <param name="limits">The configured limits, re-checked here as the aggregate's own invariant.</param>
    /// <param name="clock">The command's clock reading.</param>
    /// <exception cref="ArgumentException">
    /// The content breaks a plan §3.2 rule. The command answers such a body with 400
    /// <c>bible_invalid_content</c> before it gets here, so this throw is a programmer-error net.
    /// </exception>
    public void ReplaceContent(SeriesBibleContent content, BibleLimits limits, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);

        var errors = BibleContentValidator.Validate(content, limits);
        if (errors.Count > 0)
        {
            // The field path and the code only: a bible's content is user text and never reaches a log (D19).
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A stored bible must satisfy the plan 3.2 rules; '{errors[0].Field}' violates '{errors[0].Code}'."),
                nameof(content));
        }

        // schema_version is the backend's to write (plan §3.1): a caller that hands over another one is corrected
        // rather than trusted.
        Content = content with { SchemaVersion = SeriesBibleContent.V1 };
        Version++;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Stages a proposal for the series (plan §4 U5, F27) without touching <see cref="Version"/>: the proposal
    /// is not content, and accepting it is what bumps the version.
    /// </summary>
    /// <param name="proposal">The ops the model returned, already post-processed by the job.</param>
    /// <param name="fromEpisodeId">The approved episode the ops were computed from.</param>
    /// <param name="clock">The job's clock reading.</param>
    /// <exception cref="ArgumentException"><paramref name="fromEpisodeId"/> is <see cref="Guid.Empty"/>.</exception>
    public void SetProposal(BibleProposal proposal, Guid fromEpisodeId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(clock);

        if (fromEpisodeId == Guid.Empty)
        {
            throw new ArgumentException("A proposal needs the episode it came from.", nameof(fromEpisodeId));
        }

        ProposedContent = proposal;
        ProposedFromEpisodeId = fromEpisodeId;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Clears the pending proposal (plan §4 U3): no version bump, because the proposal was never content.
    /// </summary>
    /// <param name="clock">The command's clock reading.</param>
    public void ClearProposal(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        ProposedContent = null;
        ProposedFromEpisodeId = null;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Applies the pending ops onto the current content and clears the proposal (plan §4 U4): append-only, so an
    /// accept can never conflict with a manual edit (F27), and the version is bumped once whatever the ops did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="summarySource"/> supplies the two members of a summary the client does not own — the
    /// structure tags of the episode's approved story version and its approval instant (plan §4 U2). The op list
    /// carries no episode id, so the caller resolves the episode the summaries belong to (the row's
    /// <c>proposed_from_episode_id</c>); a summary for any other episode is skipped by the one-per-episode rule.
    /// </para>
    /// <para>
    /// An op is skipped — never stored partially — when it adds nothing (a blank field), repeats an existing
    /// entry after <see cref="BibleOpNormalizer.Normalize"/>, finds its array at the plan §3.2 limit, or would
    /// push the canonical document past <c>MaxContentBytes</c>. Text longer than its field's limit is truncated on
    /// a word boundary rather than refused, the same way an over-long summary is (plan §4 U4): an accept never
    /// fails for length.
    /// </para>
    /// </remarks>
    /// <param name="limits">The configured limits.</param>
    /// <param name="summarySource">The episode the summary ops belong to, with its structure and approval instant.</param>
    /// <param name="clock">The command's clock reading.</param>
    /// <returns>The ops that changed the content and how many were skipped; ids and counts only (D13/D19).</returns>
    /// <exception cref="InvalidOperationException">There is no pending proposal.</exception>
    public BibleApplyResult ApplyProposal(BibleLimits limits, BibleSummarySource summarySource, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(summarySource);
        ArgumentNullException.ThrowIfNull(clock);

        var proposal = ProposedContent
            ?? throw new InvalidOperationException("There is no pending proposal to apply.");

        var content = Content;
        var applied = new List<BibleOp>(proposal.Ops.Count);
        var skipped = 0;

        foreach (var op in proposal.Ops)
        {
            ArgumentNullException.ThrowIfNull(op);

            var candidate = Apply(content, op, limits, summarySource);
            if (candidate is null || BibleJson.Utf8ByteCount(candidate) > limits.MaxContentBytes)
            {
                skipped++;
                continue;
            }

            content = candidate;
            applied.Add(op);
        }

        Content = content;
        Version++;
        UpdatedAt = clock.UtcNow;
        ProposedContent = null;
        ProposedFromEpisodeId = null;

        return new BibleApplyResult(applied, skipped);
    }

    /// <summary>
    /// The content one op adds, or <see langword="null"/> when the op adds nothing (the caller counts it as
    /// skipped). Only the four append kinds exist: no op updates, deletes or touches the narrator.
    /// </summary>
    /// <param name="content">The content as it stands, including the ops applied before this one.</param>
    /// <param name="op">The op to apply.</param>
    /// <param name="limits">The configured limits.</param>
    /// <param name="summarySource">The episode a summary op belongs to.</param>
    /// <returns>The content with the op applied, or <see langword="null"/>.</returns>
    private static SeriesBibleContent? Apply(
        SeriesBibleContent content,
        BibleOp op,
        BibleLimits limits,
        BibleSummarySource summarySource)
    {
        switch (op.Kind)
        {
            case BibleOpKind.AddSummary:
                {
                    if (string.IsNullOrWhiteSpace(op.Summary) || string.IsNullOrWhiteSpace(op.Name))
                    {
                        return null;
                    }

                    foreach (var existing in content.EpisodeSummaries)
                    {
                        if (existing.EpisodeId == summarySource.EpisodeId)
                        {
                            return null;
                        }
                    }

                    return content with
                    {
                        EpisodeSummaries =
                        [
                            .. content.EpisodeSummaries,
                        new BibleEpisodeSummary(
                            summarySource.EpisodeId,
                            BibleOpNormalizer.Truncate(op.Name, limits.SummaryTitleChars),
                            BibleOpNormalizer.Truncate(op.Summary, limits.SummaryChars),
                            summarySource.Structure,
                            summarySource.ApprovedAt),
                    ],
                    };
                }

            case BibleOpKind.AddCharacter:
                {
                    if (string.IsNullOrWhiteSpace(op.Name) || content.RecurringCharacters.Count >= limits.MaxCharacters)
                    {
                        return null;
                    }

                    if (ContainsName(content.RecurringCharacters, static character => character.Name, op.Name))
                    {
                        return null;
                    }

                    return content with
                    {
                        RecurringCharacters =
                        [
                            .. content.RecurringCharacters,
                        new BibleCharacter(
                            NextId("ch", content.RecurringCharacters, static character => character.Id),
                            BibleOpNormalizer.Truncate(op.Name, limits.NameChars),
                            BibleOpNormalizer.Truncate(op.Description, limits.DescriptionChars),
                            // The op carries no episode, so the first appearance stays unknown rather than wrong.
                            null),
                    ],
                    };
                }

            case BibleOpKind.AddPlace:
                {
                    if (string.IsNullOrWhiteSpace(op.Name) || content.Places.Count >= limits.MaxPlaces)
                    {
                        return null;
                    }

                    if (ContainsName(content.Places, static place => place.Name, op.Name))
                    {
                        return null;
                    }

                    return content with
                    {
                        Places =
                        [
                            .. content.Places,
                        new BiblePlace(
                            NextId("pl", content.Places, static place => place.Id),
                            BibleOpNormalizer.Truncate(op.Name, limits.NameChars),
                            BibleOpNormalizer.Truncate(op.Description, limits.DescriptionChars)),
                    ],
                    };
                }

            case BibleOpKind.AddRule:
                {
                    if (string.IsNullOrWhiteSpace(op.Text) || content.Rules.Count >= limits.MaxRules)
                    {
                        return null;
                    }

                    if (ContainsName(content.Rules, static rule => rule.Text, op.Text))
                    {
                        return null;
                    }

                    return content with
                    {
                        Rules =
                        [
                            .. content.Rules,
                        new BibleRule(
                            NextId("rl", content.Rules, static rule => rule.Id),
                            BibleOpNormalizer.Truncate(op.Text, limits.RuleChars)),
                    ],
                    };
                }

            default:
                // A kind outside the closed four (a stored document from another build): it adds nothing.
                return null;
        }
    }

    /// <summary>Whether an array already carries an entry whose normalised text equals the op's.</summary>
    private static bool ContainsName<T>(IReadOnlyList<T> entries, Func<T, string?> text, string? candidate)
    {
        var normalised = BibleOpNormalizer.Normalize(candidate);
        if (normalised.Length == 0)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (BibleOpNormalizer.Normalize(text(entry)) == normalised)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The content of a manual save with an id for every entry that arrived without one (02.6.3 plan §4 U2): a
    /// blank or absent <c>id</c> means the client is adding the entry and does not know its id yet, so the same
    /// <c>max(existing numeric suffix) + 1</c> rule the append-only ops use supplies it. This is the one place an
    /// id is allocated, and it runs before <see cref="BibleContentValidator"/> checks the shape, the prefix and
    /// the uniqueness of every id.
    /// </summary>
    /// <remarks>
    /// The highest suffix is taken over the request's own ids <em>and</em> the stored content's, so a kept
    /// client id is never handed out again, an id of the stored content is never reused, and two blank ids of one
    /// request get two different ones. An entry that carries an id is kept verbatim — the validator decides
    /// whether the shape is acceptable — and the arrays the client did not touch are returned as they arrived.
    /// </remarks>
    /// <param name="requested">The content of the request, with the server-derived summary members already filled.</param>
    /// <param name="stored">The content as it stands, whose ids the new ones must not reuse.</param>
    /// <returns>The content with every blank id replaced; entries with an id are unchanged.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public static SeriesBibleContent AssignMissingIds(SeriesBibleContent requested, SeriesBibleContent stored)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(stored);

        return requested with
        {
            RecurringCharacters = AssignIds(
                "ch",
                requested.RecurringCharacters,
                stored.RecurringCharacters,
                static character => character.Id,
                static (character, id) => character with { Id = id }),
            Places = AssignIds(
                "pl",
                requested.Places,
                stored.Places,
                static place => place.Id,
                static (place, id) => place with { Id = id }),
            Rules = AssignIds(
                "rl",
                requested.Rules,
                stored.Rules,
                static rule => rule.Id,
                static (rule, id) => rule with { Id = id }),
        };
    }

    /// <summary>
    /// One array with an id for every entry that arrived without one: the first blank takes one past the highest
    /// suffix of the stored ids and the request's own ids, and every further blank the next number.
    /// </summary>
    /// <typeparam name="T">The entry type (a character, a place or a rule).</typeparam>
    /// <param name="prefix">The prefix the array owns (<c>ch</c>, <c>pl</c> or <c>rl</c>).</param>
    /// <param name="requested">The entries of the request, in body order.</param>
    /// <param name="stored">The entries the content currently holds.</param>
    /// <param name="id">How to read one entry's id.</param>
    /// <param name="withId">How to copy one entry with a new id.</param>
    /// <returns>The entries, or <paramref name="requested"/> itself when every entry already carries an id.</returns>
    private static IReadOnlyList<T> AssignIds<T>(
        string prefix,
        IReadOnlyList<T> requested,
        IReadOnlyList<T> stored,
        Func<T, string?> id,
        Func<T, string, T> withId)
    {
        var next = Math.Max(HighestSuffix(prefix, stored, id), HighestSuffix(prefix, requested, id)) + 1;

        List<T>? assigned = null;
        for (var index = 0; index < requested.Count; index++)
        {
            var entry = requested[index];
            if (!string.IsNullOrWhiteSpace(id(entry)))
            {
                assigned?.Add(entry);
                continue;
            }

            assigned ??= new List<T>(requested.Take(index));
            assigned.Add(withId(entry, string.Create(CultureInfo.InvariantCulture, $"{prefix}{next}")));
            next++;
        }

        return assigned ?? requested;
    }

    /// <summary>
    /// The next free id of one prefix: <c>max(existing numeric suffix) + 1</c> over the content as it stands
    /// (plan §3.1, §4 U4), so ids are never renumbered and a prefix is never crossed. An id that is the highest
    /// one and was removed by a manual edit is indistinguishable from one that was never assigned, which is what
    /// makes max+1 the rule (<c>ch1</c>, <c>ch3</c> → <c>ch4</c>).
    /// </summary>
    private static string NextId<T>(string prefix, IReadOnlyList<T> entries, Func<T, string?> id) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}{HighestSuffix(prefix, entries, id) + 1}");

    /// <summary>
    /// The highest numeric suffix the ids of one array carry for <paramref name="prefix"/>, or zero. The one
    /// allocation rule of the feature: <see cref="NextId{T}"/> and <see cref="AssignMissingIds"/> both take their
    /// next id from it.
    /// </summary>
    /// <param name="prefix">The prefix the array owns (<c>ch</c>, <c>pl</c> or <c>rl</c>).</param>
    /// <param name="entries">The entries to scan.</param>
    /// <param name="id">How to read one entry's id.</param>
    /// <returns>The highest suffix found; zero when none is parseable.</returns>
    private static int HighestSuffix<T>(string prefix, IReadOnlyList<T> entries, Func<T, string?> id)
    {
        var highest = 0;
        foreach (var entry in entries)
        {
            var existing = id(entry);
            if (existing is not null
                && existing.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(existing.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number > highest)
            {
                highest = number;
            }
        }

        return highest;
    }
}
