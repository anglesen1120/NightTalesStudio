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
}
