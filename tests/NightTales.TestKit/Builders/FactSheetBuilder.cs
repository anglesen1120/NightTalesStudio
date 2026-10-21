using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts real <c>fact_sheets</c> rows for tests that need one: <c>generation_runs.input_fact_sheet_id</c>,
/// <c>generation_runs.result_fact_sheet_id</c> and <c>story_versions.fact_sheet_id</c> are same-owner foreign
/// keys to <c>fact_sheets (id, user_id)</c> (02 CR-29), so a pinned or seeded id must name a real row of the
/// same episode and owner.
/// </summary>
/// <remarks>
/// The row is materialized through the domain factory <see cref="FactSheet.CreateDraft"/>, so the canonical
/// documents, the content hash and the CHECK-constrained columns are the production ones.
/// </remarks>
/// <param name="database">The test database.</param>
/// <param name="currentUser">The tenant the insert runs as.</param>
/// <param name="clock">The clock of the insert, or <see langword="null"/> for the interceptor's stamp.</param>
public sealed class FactSheetBuilder(TestDatabase database, TestCurrentUser currentUser, IClock? clock = null)
{
    /// <summary>The prompt version a hand-created draft carries.</summary>
    public const string DefaultPromptVersion = "user";

    /// <summary>
    /// Inserts one draft <c>fact_sheets</c> row of <paramref name="episodeId"/> (version 1, no facts, no run).
    /// </summary>
    /// <param name="userId">The owner; must own the episode (the FK is the same-owner alternate key).</param>
    /// <param name="episodeId">The episode the sheet belongs to.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>The inserted sheet, with its generated id.</returns>
    public async Task<FactSheet> CreateAsync(Guid userId, Guid episodeId, CancellationToken ct = default)
    {
        await using var context = database.CreateAppDbContext(currentUser, clock);

        var sheet = FactSheet.CreateDraft(
            userId,
            episodeId,
            1,
            [],
            new Dictionary<string, Guid>(StringComparer.Ordinal),
            inputTruncated: false,
            maxFactId: 0,
            Guid.Empty,
            DefaultPromptVersion,
            clock?.UtcNow ?? DateTimeOffset.UtcNow);

        context.FactSheets.Add(sheet);
        await context.SaveChangesAsync(ct);

        return sheet;
    }

    /// <summary>
    /// Inserts one <c>fact_sheets</c> row of an explicit version, status and fact list (02.5.4-T1): the tests
    /// of the edit/approve routes need a version other than the first, a row that is already
    /// <see cref="FactSheetStatus.Approved"/> (supersede, re-approve) or a draft whose facts cite sources of
    /// the episode (F13).
    /// </summary>
    /// <remarks>
    /// The facts go through <see cref="FactSheet.CreateDraft"/>, so every invariant of the stored document
    /// holds (ids <c>f1…fN</c>, every citation present in <paramref name="sourceAliases"/>, the content hash
    /// recomputed). The status and the approval stamps are written through the change tracker — the same path
    /// the approve handler takes — because a seeded approved or superseded row has no factory.
    /// </remarks>
    /// <param name="userId">The owner; must own the episode (the FK is the same-owner alternate key).</param>
    /// <param name="episodeId">The episode the sheet belongs to.</param>
    /// <param name="version">The 1-based version the row carries; unique per episode.</param>
    /// <param name="facts">The stored facts, in output order; may be empty.</param>
    /// <param name="sourceAliases">The alias→source-id map the row carries; must cover every cited id.</param>
    /// <param name="maxFactId">The episode's id watermark, at least the highest id in <paramref name="facts"/> (F12).</param>
    /// <param name="status">The lifecycle state to write (draft, approved or superseded).</param>
    /// <param name="approvedBy">The approver of an approved row.</param>
    /// <param name="approvedAt">The approval stamp of an approved row.</param>
    /// <param name="promptVersion">The D5 prompt version; <see cref="DefaultPromptVersion"/> for a hand row.</param>
    /// <param name="generationRunId">The run that produced the row, or <see langword="null"/> for a hand row.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>The inserted sheet, with its generated id.</returns>
    public async Task<FactSheet> CreateVersionAsync(
        Guid userId,
        Guid episodeId,
        int version,
        IReadOnlyList<FactItem> facts,
        IReadOnlyDictionary<string, Guid> sourceAliases,
        int maxFactId = 0,
        FactSheetStatus status = FactSheetStatus.Draft,
        Guid? approvedBy = null,
        DateTimeOffset? approvedAt = null,
        string promptVersion = DefaultPromptVersion,
        Guid? generationRunId = null,
        CancellationToken ct = default)
    {
        await using var context = database.CreateAppDbContext(currentUser, clock);

        var now = clock?.UtcNow ?? DateTimeOffset.UtcNow;
        var sheet = FactSheet.CreateDraft(
            userId,
            episodeId,
            version,
            facts,
            sourceAliases,
            inputTruncated: false,
            maxFactId,
            generationRunId ?? Guid.Empty,
            promptVersion,
            now);

        context.FactSheets.Add(sheet);

        if (status != FactSheetStatus.Draft)
        {
            ShellInstance.Set(context, sheet, nameof(FactSheet.Status), status);
        }

        if (approvedBy is { } approver)
        {
            ShellInstance.Set(context, sheet, nameof(FactSheet.ApprovedBy), approver);
        }

        if (approvedAt is { } stamp)
        {
            ShellInstance.Set(context, sheet, nameof(FactSheet.ApprovedAt), stamp);
        }

        await context.SaveChangesAsync(ct);

        return sheet;
    }
}
