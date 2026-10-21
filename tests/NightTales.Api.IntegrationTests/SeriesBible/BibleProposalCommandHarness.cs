using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Series.Bible;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;

// The enclosing namespace of this file is `…Api.IntegrationTests.SeriesBible`, so the simple name
// `SeriesBible` resolves to that namespace and never to the Domain aggregate (the trap `SeriesEndpointsTests`
// documents for `NightTales.Domain.Series`).
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The fixtures and probes of the two proposal routes (02.6.3-T2, plan §4 U3/U4): a proposal written straight
/// into <c>series_bibles.proposed_content</c> through the Domain's own <c>SetProposal</c> — the accept and the
/// dismiss do not depend on the job that normally stages one — and the reads the tests assert with (the stored
/// proposal, and the audit row of an arbitrary action, which <see cref="SeriesBibleHarness"/>'s helpers only
/// answer for <c>series.bible_updated</c>).
/// </summary>
internal static class BibleProposalCommandHarness
{
    /// <summary>The accept route of one series' bible.</summary>
    internal static string AcceptUrl(Guid seriesId) =>
        string.Concat(SeriesBibleHarness.Url(seriesId), "/proposal/accept");

    /// <summary>The dismiss route of one series' bible.</summary>
    internal static string DismissUrl(Guid seriesId) =>
        string.Concat(SeriesBibleHarness.Url(seriesId), "/proposal/dismiss");

    /// <summary>
    /// <c>POST /api/v1/series/{id}/bible/proposal/accept</c> with no body and no conditional header — the shape
    /// the route documents (plan §5).
    /// </summary>
    internal static Task<HttpResponseMessage> AcceptAsync(HttpClient client, Guid seriesId) =>
        client.PostAsync(AcceptUrl(seriesId), content: null, SeriesBibleHarness.Ct);

    /// <summary><c>POST /api/v1/series/{id}/bible/proposal/dismiss</c> with no body.</summary>
    internal static Task<HttpResponseMessage> DismissAsync(HttpClient client, Guid seriesId) =>
        client.PostAsync(DismissUrl(seriesId), content: null, SeriesBibleHarness.Ct);

    /// <summary>
    /// Stages a proposal on the series' row the way the job does — the Domain's <c>SetProposal</c> through the
    /// same <c>AppDbContext</c> production code uses, so what the routes read back is the stored <c>jsonb</c>
    /// document. No version moves: a proposal is not content.
    /// </summary>
    /// <param name="database">The class database.</param>
    /// <param name="world">The owner and series of the fixture.</param>
    /// <param name="episodeId">The approved episode the ops were computed from (<c>proposed_from_episode_id</c>).</param>
    /// <param name="ops">The ops the model returned, in model order.</param>
    /// <param name="basedOnVersion">The version the ops were computed against; the row's current one by default.</param>
    internal static async Task SeedProposalAsync(
        TestDatabase database,
        World world,
        Guid episodeId,
        IReadOnlyList<BibleOp> ops,
        int? basedOnVersion = null)
    {
        var clock = new FakeClock(SeriesBibleHarness.Start);
        await using var context = database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, clock);

        var bible = await context.SeriesBibles
            .SingleAsync(candidate => candidate.SeriesId == world.SeriesId, SeriesBibleHarness.Ct);

        bible.SetProposal(
            new BibleProposal(ops, basedOnVersion ?? bible.Version, SeriesBibleHarness.Start),
            episodeId,
            clock);

        await context.SaveChangesAsync(SeriesBibleHarness.Ct);
    }

    /// <summary>The stored <c>proposed_content</c> document, or the empty string when the row has none.</summary>
    internal static string ProposalJson(TestDatabase database, Guid seriesId) =>
        SqlProbe.ScalarText(
            database,
            $"SELECT coalesce(proposed_content::text, '') FROM series_bibles WHERE series_id = '{Key(seriesId)}'");

    /// <summary>The stored <c>proposed_from_episode_id</c>, or the empty string when the row has none.</summary>
    internal static string ProposalEpisodeId(TestDatabase database, Guid seriesId) =>
        SqlProbe.ScalarText(
            database,
            $"SELECT coalesce(proposed_from_episode_id::text, '') FROM series_bibles WHERE series_id = '{Key(seriesId)}'");

    /// <summary>One <c>audit_logs</c> row of an action and its actor, flattened for a string assertion.</summary>
    internal static string AuditRow(TestDatabase database, Guid userId, string action) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT action || '|' || category || '|' || visibility || '|' || actor_type || '|' || actor_user_id
                 || '|' || subject_user_id || '|' || coalesce(entity_type, '-') || '|' || coalesce(data::text, '-')
             FROM audit_logs
             WHERE action = '{action}' AND actor_user_id = '{Key(userId)}'
             """);

    /// <summary>How many rows of an action the actor has.</summary>
    internal static long AuditCount(TestDatabase database, Guid userId, string action) =>
        SqlProbe.Count(
            database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{action}' AND actor_user_id = '{Key(userId)}'");

    /// <summary>One member of the <c>data</c> document of an action's audit row of the actor.</summary>
    internal static string AuditDataField(TestDatabase database, Guid userId, string action, string key) =>
        SqlProbe.ScalarText(
            database,
            $"""
             SELECT coalesce(data ->> '{key}', '') FROM audit_logs
             WHERE action = '{action}' AND actor_user_id = '{Key(userId)}'
             """);

    /// <summary>An <c>add_character</c> op, the shape the model returns for a recurring character.</summary>
    /// <param name="name">The character's name.</param>
    /// <param name="description">Its description.</param>
    internal static BibleOp CharacterOp(string name, string description = "Người phụ nữ bên sông") =>
        new(BibleOpKind.AddCharacter, null, name, description, null, "xuất hiện ở tập này");

    /// <summary>An <c>add_place</c> op.</summary>
    /// <param name="name">The place's name.</param>
    internal static BibleOp PlaceOp(string name) =>
        new(BibleOpKind.AddPlace, null, name, "Căn nhà bên sông", null, "bối cảnh của tập");

    /// <summary>An <c>add_rule</c> op.</summary>
    /// <param name="text">The standing rule.</param>
    internal static BibleOp RuleOp(string text) =>
        new(BibleOpKind.AddRule, null, null, null, text, "luật của bộ truyện");

    /// <summary>
    /// An <c>add_summary</c> op. The op carries no episode — the backend binds it to the row's
    /// <c>proposed_from_episode_id</c> and derives its structure and approval instant from that episode's
    /// approved story version (plan §4 U4).
    /// </summary>
    /// <param name="title">The summary's title.</param>
    /// <param name="summary">The summary's text.</param>
    internal static BibleOp SummaryOp(string title, string summary) =>
        new(BibleOpKind.AddSummary, summary, title, null, null, "tóm tắt tập vừa duyệt");

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
