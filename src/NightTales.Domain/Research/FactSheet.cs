using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    /// <exception cref="ArgumentException">A fact cites no source, cites a source missing from the alias map, carries a malformed or repeated id, or a required owner/episode/prompt version is empty.</exception>
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
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(sourceAliases);

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A fact sheet needs an owner.", nameof(userId));
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

            if (!TryFactIdNumber(fact.Id, out var number))
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
            UserId = userId,
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
            CreatedBy = userId,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    /// <summary>
    /// Demotes a version to <see cref="FactSheetStatus.Superseded"/> in the same transaction that approves a
    /// newer one (T54), so at most one row per episode stays approved — 02.5.4-T1 is the only caller and it
    /// calls this on the episode's previously approved row. The flip is idempotent (re-demoting an already
    /// superseded version is a no-op) and never throws: invariant 4 itself is enforced by the partial unique
    /// index <c>ux_fact_sheets_episode_approved</c> plus that transaction, not by this method.
    /// </summary>
    public void Supersede() => Status = FactSheetStatus.Superseded;

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

    /// <summary>Parses a backend fact id (<c>f</c> plus a 1-based number, no leading zero).</summary>
    private static bool TryFactIdNumber(string? id, out int number)
    {
        number = 0;
        return id is { Length: >= 2 }
            && id[0] == 'f'
            && id[1] != '0'
            && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number >= 1;
    }
}
