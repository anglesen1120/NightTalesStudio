using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Domain.Research;

/// <summary>
/// One fact-sheet version of an episode (02.5.3 plan §3, D9): the facts the writer may use, the aliases they
/// cite and the metadata that ties the version to the generation run that produced it. A row is never edited
/// in place — editing creates a new version (D9: the ETag is <c>fs-&lt;id&gt;</c>) — so the facts, their ids
/// and the hash of the stored document are immutable once the row exists; only <see cref="Status"/> moves
/// (<see cref="FactSheetStatus.Draft"/> → <see cref="FactSheetStatus.Approved"/> →
/// <see cref="FactSheetStatus.Superseded"/>, T54).
/// </summary>
public sealed class FactSheet : Entity, IUserOwned, IAuditable
{
    /// <summary>
    /// The catalogued code of "the edit or the approve does not address the current version" (412, 02 README
    /// D8/D9). The Domain may not reference <c>NightTales.Application.Common.Errors.ErrorCodes</c> (ErrorOr is
    /// its only package), so it spells the code itself; the unit test
    /// <c>FactSheet_Codes_MatchTheErrorCatalogue</c> keeps the two equal.
    /// </summary>
    public const string ConflictCode = "fact_sheet_conflict";

    /// <summary>The <see cref="PromptVersion"/> a hand-edited version writes: a person, not a D5 prompt bundle.</summary>
    public const string UserPromptVersion = "user";

    private FactSheet()
    {
    }

    /// <summary>The owner of the episode; the tenant filter and the composite fact-sheet FKs key on it (CR-46).</summary>
    public Guid UserId { get; private set; }

    /// <summary>The episode the fact sheet belongs to.</summary>
    public Guid EpisodeId { get; private init; }

    /// <summary>The 1-based, monotonic-per-episode version number (<c>max(version) + 1</c> at creation).</summary>
    public int Version { get; private init; }

    /// <summary>Where the version is in its lifecycle.</summary>
    public FactSheetStatus Status { get; private set; }

    /// <summary>The stored facts, in output order; the source of <see cref="ContentSha256"/>.</summary>
    public IReadOnlyList<FactItem> Facts { get; private set; } = [];

    /// <summary>
    /// The alias→<c>story_sources.id</c> map written on every version (CR-4/D21), so a later source edit can
    /// never corrupt the citation a stored fact carries.
    /// </summary>
    public IReadOnlyDictionary<string, Guid> SourceAliases { get; private set; } =
        new Dictionary<string, Guid>(StringComparer.Ordinal);

    /// <summary>Whether the extraction input was truncated (<c>FactInputBudget</c>, CR-4).</summary>
    public bool InputTruncated { get; private init; }

    /// <summary>The highest fact id ever assigned in the episode; only ever grows across versions (F12).</summary>
    public int MaxFactId { get; private set; }

    /// <summary>The 64 lowercase hex SHA-256 of <see cref="Facts"/>' canonical JSON (plan §3 invariant 3).</summary>
    public string ContentSha256 { get; private set; } = string.Empty;

    /// <summary>The run that produced the version; null for a hand-edited version (D9).</summary>
    public Guid? GenerationRunId { get; private init; }

    /// <summary>The D5 prompt version, or <c>"user"</c> for a hand edit.</summary>
    public string PromptVersion { get; private init; } = string.Empty;

    /// <summary>The user who created the version.</summary>
    public Guid CreatedBy { get; private init; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The user who approved the version; null until approved (02.5.4-T1).</summary>
    public Guid? ApprovedBy { get; private set; }

    /// <summary>When the version was approved; null until approved (02.5.4-T1).</summary>
    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>
    /// Creates the draft version a generation run saved (02.5.3-T2 step 2): the first version of the episode,
    /// or the next one. The facts must already carry backend-assigned ids (<c>f1…fN</c>) and cite at least one
    /// source that is present in <paramref name="sourceAliases"/>; those are programmer errors and throw,
    /// because the validator (Application) has already turned every model-output failure into a drop.
    /// </summary>
    /// <param name="userId">The owner of the episode; also recorded as <see cref="CreatedBy"/>.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="version">The 1-based version number.</param>
    /// <param name="facts">The validated facts, in output order; empty when none survived.</param>
    /// <param name="sourceAliases">The alias→source-id map the run persisted (CR-4/D21).</param>
    /// <param name="inputTruncated">Whether the extraction input was truncated (CR-4).</param>
    /// <param name="maxFactId">The highest fact id assigned so far in the episode; at least the highest id in <paramref name="facts"/>.</param>
    /// <param name="generationRunId">The run that produced the version; <see cref="Guid.Empty"/> stores null.</param>
    /// <param name="promptVersion">The D5 prompt version of the run.</param>
    /// <param name="createdAt">The job's clock reading.</param>
    /// <returns>The new draft version, with <see cref="ContentSha256"/> computed.</returns>
    /// <exception cref="ArgumentException">A fact cites no source, cites a source missing from the alias map, names a source alias the map does not carry, carries a malformed or repeated id, or a required owner/episode/prompt version is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The version is below 1, <paramref name="maxFactId"/> is negative or below the highest fact id.</exception>
    public static FactSheet CreateDraft(
        Guid userId,
        Guid episodeId,
        int version,
        IReadOnlyList<FactItem> facts,
        IReadOnlyDictionary<string, Guid> sourceAliases,
        bool inputTruncated,
        int maxFactId,
        Guid generationRunId,
        string promptVersion,
        DateTimeOffset createdAt) =>
        CreateVersion(
            ownerUserId: userId,
            createdBy: userId,
            episodeId: episodeId,
            version: version,
            facts: facts,
            sourceAliases: sourceAliases,
            inputTruncated: inputTruncated,
            maxFactId: maxFactId,
            generationRunId: generationRunId,
            promptVersion: promptVersion,
            createdAt: createdAt);

    /// <summary>
    /// Creates the next version of an episode from a hand edit (02.5.4-T1): <c>previous.Version + 1</c>, a draft,
    /// no generation run and the <c>"user"</c> prompt version, because a person wrote this body rather than the
    /// model. Every <see cref="CreateDraft"/> invariant still holds — the facts carry backend ids, cite sources
    /// the alias map names and stay within the watermark — so an invalid body cannot reach the database through
    /// this path either; the overlap and confidence semantics of the edit belong to the caller (T53).
    /// </summary>
    /// <remarks>
    /// The previous version is only read: it supplies the episode, the owner, the version counter and the
    /// truncation flag, and the transaction that stores the result leaves its row untouched (D9 — the ETag is
    /// the row id, so an edit always adds a row). Its status does not matter: editing an approved version is
    /// how the next draft begins (02 §4).
    /// </remarks>
    /// <param name="previous">The episode's current version, whatever its status.</param>
    /// <param name="facts">The new version's facts, in output order.</param>
    /// <param name="sourceAliases">The alias map the new version stores (CR-4/D21).</param>
    /// <param name="maxFactId">The episode's watermark, which <see cref="FactIdAllocator"/> allocated the new ids from.</param>
    /// <param name="userId">The user the edit belongs to; recorded as <see cref="CreatedBy"/>.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>The new draft version, with <see cref="ContentSha256"/> computed.</returns>
    /// <exception cref="ArgumentException">A required owner, episode or prompt version is empty, or a fact breaks an invariant of <see cref="CreateDraft"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The watermark is negative or below the highest fact id.</exception>
    public static FactSheet CreateUserDraft(
        FactSheet previous,
        IReadOnlyList<FactItem> facts,
        IReadOnlyDictionary<string, Guid> sourceAliases,
        int maxFactId,
        Guid userId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(previous);

        return CreateVersion(
            ownerUserId: previous.UserId,
            createdBy: userId,
            episodeId: previous.EpisodeId,
            version: previous.Version + 1,
            facts: facts,
            sourceAliases: sourceAliases,
            inputTruncated: previous.InputTruncated,
            maxFactId: maxFactId,
            generationRunId: Guid.Empty,
            promptVersion: UserPromptVersion,
            createdAt: now);
    }

    /// <summary>
    /// The one draft factory: the run path and the hand-edit path differ only in who is recorded as the creator,
    /// so the invariants a stored version must satisfy are checked in a single place.
    /// </summary>
    private static FactSheet CreateVersion(
        Guid ownerUserId,
        Guid createdBy,
        Guid episodeId,
        int version,
        IReadOnlyList<FactItem> facts,
        IReadOnlyDictionary<string, Guid> sourceAliases,
        bool inputTruncated,
        int maxFactId,
        Guid generationRunId,
        string promptVersion,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(sourceAliases);

        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("A fact sheet needs an owner.", nameof(ownerUserId));
        }

        if (episodeId == Guid.Empty)
        {
            throw new ArgumentException("A fact sheet needs an episode.", nameof(episodeId));
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "A fact-sheet version is 1-based.");
        }

        if (maxFactId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFactId), maxFactId, "max_fact_id cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(promptVersion))
        {
            throw new ArgumentException("A fact sheet needs its prompt version.", nameof(promptVersion));
        }

        var knownSourceIds = new HashSet<Guid>(sourceAliases.Values);
        var assignedIds = new HashSet<string>(StringComparer.Ordinal);
        var highestFactId = 0;
        foreach (var fact in facts)
        {
            ArgumentNullException.ThrowIfNull(fact);
            if (fact.SourceAliases is null)
            {
                throw new ArgumentException("Every fact must carry its source aliases.", nameof(facts));
            }

            if (fact.SourceIds is null || fact.SourceIds.Count == 0)
            {
                throw new ArgumentException("Every fact must cite at least one source.", nameof(facts));
            }

            foreach (var sourceId in fact.SourceIds)
            {
                if (!knownSourceIds.Contains(sourceId))
                {
                    throw new ArgumentException(
                        $"Fact source '{sourceId}' is not in the fact sheet's source_aliases.",
                        nameof(facts));
                }
            }

            // The citation a stored fact carries is the pair (alias, id): an alias the map does not name is as
            // unusable as an id it does not carry, because a later reader resolves the alias through this map.
            foreach (var alias in fact.SourceAliases)
            {
                if (!sourceAliases.ContainsKey(alias))
                {
                    throw new ArgumentException(
                        $"Fact source alias '{alias}' is not a key of the fact sheet's source_aliases.",
                        nameof(facts));
                }
            }

            if (!FactIdAllocator.TryParseNumber(fact.Id, out var number))
            {
                throw new ArgumentException($"Fact id '{fact.Id}' must be 'f' followed by its 1-based number.", nameof(facts));
            }

            if (!assignedIds.Add(fact.Id))
            {
                throw new ArgumentException($"Fact id '{fact.Id}' is repeated within one fact sheet.", nameof(facts));
            }

            highestFactId = Math.Max(highestFactId, number);
        }

        if (maxFactId < highestFactId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFactId),
                maxFactId,
                $"max_fact_id must be at least the highest fact id ({highestFactId}).");
        }

        return new FactSheet
        {
            Id = Guid.CreateVersion7(),
            UserId = ownerUserId,
            EpisodeId = episodeId,
            Version = version,
            Status = FactSheetStatus.Draft,
            Facts = facts,
            SourceAliases = sourceAliases,
            InputTruncated = inputTruncated,
            MaxFactId = maxFactId,
            ContentSha256 = ComputeContentSha256(facts),
            GenerationRunId = generationRunId == Guid.Empty ? null : generationRunId,
            PromptVersion = promptVersion,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    /// <summary>
    /// Demotes the version to <see cref="FactSheetStatus.Superseded"/> in the same transaction that approves a
    /// newer one (T54), so at most one row per episode stays approved — 02.5.4-T1 is the only caller and it
    /// calls this on the episode's previously approved row.
    /// </summary>
    /// <remarks>
    /// Only an <see cref="FactSheetStatus.Approved"/> row moves: a draft is the row an edit just created and a
    /// superseded row is already there, so both are left exactly as they are, and no other property is touched
    /// (invariant 4 itself is enforced by the partial unique index <c>ux_fact_sheets_episode_approved</c> plus
    /// that transaction, not by this method).
    /// </remarks>
    public void Supersede()
    {
        if (Status == FactSheetStatus.Approved)
        {
            Status = FactSheetStatus.Superseded;
        }
    }

    /// <summary>
    /// Approves the version (02.5.4-T1): only a draft can be approved, so a repeated approve — or one that
    /// addresses a row that has since been superseded — is a 412 <see cref="ConflictCode"/> rather than a
    /// second approval.
    /// </summary>
    /// <remarks>
    /// The caller owns the ETag check, the episode state gate and the F13 source-diversity gate (D9/F13), and
    /// it must demote the episode's previously approved row in the same transaction before this runs, because
    /// the partial unique index allows one approved row per episode.
    /// </remarks>
    /// <param name="approvedBy">The approving user.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns><c>Success</c>, or the 412 conflict when the version is not a draft.</returns>
    public ErrorOr<Success> Approve(Guid approvedBy, DateTimeOffset now)
    {
        if (Status != FactSheetStatus.Draft)
        {
            return Error.Conflict(ConflictCode, "Only the current draft of a fact sheet can be approved.");
        }

        Status = FactSheetStatus.Approved;
        ApprovedBy = approvedBy;
        ApprovedAt = now;
        UpdatedAt = now;

        return Result.Success;
    }

    /// <summary>
    /// The SHA-256 (64 lowercase hex) over the canonical JSON of the facts (plan §3 invariant 3,
    /// <see cref="FactSheetJson"/>): the same facts always hash to the same value, so a change to the stored
    /// document is visible without reading the rows.
    /// </summary>
    /// <param name="facts">The facts in output order.</param>
    /// <returns>The 64-character lowercase hex hash.</returns>
    public static string ComputeContentSha256(IReadOnlyList<FactItem> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var json = FactSheetJson.SerializeFacts(facts);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
