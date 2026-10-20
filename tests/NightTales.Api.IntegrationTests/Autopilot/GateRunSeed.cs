using System.Globalization;
using System.Text.Json.Nodes;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Usage;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The stored world one story-gate evaluation needs (02.8.2-T1): an owner, a series, an episode in
/// <c>story_review</c> whose current <c>story_versions</c> row carries a well-formed <c>audit.v1</c> document, a
/// D4-band sized body, a stored similarity score, one past episode of the same series (check 10's corpus) and an
/// <c>autopilot_runs</c> row inserted through <see cref="AutopilotRunBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value is deterministic and marker-bearing.</b> <see cref="StoryMarker"/> appears in the story body and
/// <see cref="SourceUrlText"/> in the episode brief: the tests assert neither reaches <c>gate_results</c>, an audit
/// row or an outbox payload (D19/D22).
/// </para>
/// <para>
/// <b>The band.</b> The story body carries 212 D4 tokens, inside the <c>short</c> × <c>vi</c> cell of
/// <see cref="PriceLimits.D4Defaults"/> (195–260, ±15 % advisory), which
/// <see cref="SeedPriceVersionAsync"/> writes as the active price version: check 11 has no closed value, so a host
/// without that row would answer an error instead of a band.
/// </para>
/// </remarks>
internal static class GateRunSeed
{
    /// <summary>The marker inside the story body: it must never appear in a verdict, an audit row or a payload.</summary>
    internal const string StoryMarker = "zzlanternmarkerzz";

    /// <summary>The marker inside the episode brief, the D19-banned field nearest the gate's reads.</summary>
    internal const string SourceUrlText = "https://example.invalid/gate-source-leak";

    /// <summary>The <c>audit.v1</c> moderation block of a clean, current draft-time verdict.</summary>
    internal const string CleanModeration =
        """{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}""";

    /// <summary><c>revision.applied = false</c>, which check 5 reads as zero revisions.</summary>
    internal const string NotApplied = """{"applied":false}""";

    /// <summary>The deterministic instant every seeded row and every host clock is stamped with.</summary>
    internal static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>One seeded world; a test names the run it evaluates and the rows it asserts about.</summary>
    /// <param name="UserId">The owner (and the actor every hop is pinned to).</param>
    /// <param name="SeriesId">The series the run belongs to.</param>
    /// <param name="EpisodeId">The episode the run names.</param>
    /// <param name="VersionId">Its current story version (the one the gate evaluates and the ETag names).</param>
    /// <param name="SecondVersionId">A second version of the same episode, for the stale-ETag scenario.</param>
    /// <param name="RunId">The <c>autopilot_runs</c> row.</param>
    /// <param name="PastEpisodeId">The past episode of the series, check 10's corpus.</param>
    /// <param name="PastVersionId">That episode's current story version.</param>
    internal sealed record GateWorld(
        Guid UserId,
        Guid SeriesId,
        Guid EpisodeId,
        Guid VersionId,
        Guid SecondVersionId,
        Guid RunId,
        Guid PastEpisodeId,
        Guid PastVersionId);

    /// <summary>
    /// The six D16 dimensions of one review, all scoring the same value, plus the <c>real_world_claims</c>
    /// member every report carries: its absence is unknown evidence (check 8 fails closed), not "no hit".
    /// </summary>
    /// <param name="value">The score every dimension carries (1–5).</param>
    internal static string ReviewJson(int value) =>
        "{\"stale\":false,\"scores\":{\"hook\":" + Number(value) + ",\"pacing\":" + Number(value)
        + ",\"atmosphere\":" + Number(value) + ",\"natural_language\":" + Number(value)
        + ",\"originality\":" + Number(value) + ",\"length_fit\":" + Number(value)
        + "},\"issues\":[],\"real_world_claims\":[]}";

    /// <summary>A complete <c>audit.v1</c> document around one review, its moderation block and its revision.</summary>
    /// <param name="modelReview">The <c>model_review</c> object, from <see cref="ReviewJson"/>.</param>
    /// <param name="moderation">The moderation block, or null to omit it (the gate then moderates itself).</param>
    /// <param name="revision">The revision block; <see cref="NotApplied"/> by default.</param>
    internal static string AuditReport(string modelReview, string? moderation = CleanModeration, string revision = NotApplied) =>
        "{\"schema_version\":\"audit.v1\",\"local\":{\"issues\":[]}"
        + (moderation is null ? string.Empty : ",\"moderation\":" + moderation)
        + ",\"model_review\":" + modelReview + ",\"revision\":" + revision + "}";

    /// <summary>The audit of a passed review: every dimension at <paramref name="score"/>.</summary>
    /// <param name="score">The dimension score; 4 clears the default <c>3.5</c> mean floor.</param>
    internal static string CleanAudit(int score = 4) => AuditReport(ReviewJson(score));

    /// <summary>
    /// The audit of a review whose mean score is 3.0: check 2 (<c>self_review_mean_score</c>, floor 3.5) fails and
    /// every other check still passes, so the failing check list is exactly one id.
    /// </summary>
    internal static string FailingAudit() => AuditReport(ReviewJson(3));

    /// <summary>
    /// The audit of a revised version: <c>model_review</c> is review #1 (mean 3.0) and <c>revision.applied</c> is
    /// true, so the checks must read <c>revision.review_after</c> (review #2, all 5s) and pass.
    /// </summary>
    internal static string RevisedAudit() =>
        AuditReport(
            ReviewJson(3),
            CleanModeration,
            "{\"applied\":true,\"review_after\":" + ReviewJson(5) + "}");

    /// <summary>The episode brief: the D19-banned field that carries <see cref="SourceUrlText"/>.</summary>
    internal static string Brief => $"A gate probe episode about {SourceUrlText}";

    /// <summary>The story draft every seed stores: a 212-token body inside the short × vi band.</summary>
    internal static StoryContentV1 Draft() => new(
        "The lantern on the pier",
        new StorySegmentV1(StoryMarker + " " + Words(25, 0, MainLexicon), ["lantern keeper on the pier"], [], null),
        [
            new StorySegmentV1(Words(60, 3, MainLexicon), ["quiet night by the harbour"], [], "setup"),
            new StorySegmentV1(Words(62, 11, MainLexicon), ["old wooden pier at dawn"], [], null),
            new StorySegmentV1(Words(48, 19, MainLexicon), ["empty path along the water"], [], null),
        ],
        new StorySegmentV1(Words(16, 27, MainLexicon), ["cold morning by the water"], [], null),
        "A quiet story about a forgotten lantern.",
        ["ghost", "night"],
        ["supernatural"],
        new StoryStructureV1("ghost", "pier", "a light", "the keeper", "first_person"));

    /// <summary>Writes the active price version with the D4 defaults, once per database.</summary>
    /// <param name="database">The class's own database.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task SeedPriceVersionAsync(TestDatabase database, CancellationToken ct = default)
    {
        if (SqlProbe.Count(database, "SELECT count(*) FROM price_versions WHERE version = 1") > 0)
        {
            return;
        }

        await database.ExecuteAsync(
            "INSERT INTO price_versions (version, active_from, rates, limits, config_hash, created_at) VALUES (1, '"
            + Instant(Start.AddDays(-1)) + "', '{\"text\":{}}'::jsonb, '"
            + PriceVersionJson.WriteLimits(PriceLimits.D4Defaults) + "'::jsonb, 'gate-price-version', '"
            + Instant(Start) + "')", ct);
    }

    /// <summary>Inserts one <c>autopilot_runs</c> row through the TestKit builder and returns its id.</summary>
    /// <param name="database">The class's own database.</param>
    /// <param name="userId">The run's owner.</param>
    /// <param name="seriesId">The run's series.</param>
    /// <param name="episodeId">The run's episode.</param>
    /// <param name="startedAt">The run's slot instant (its <c>started_at</c> and <c>slot_at</c> alike).</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task<Guid> SeedRunRowAsync(
        TestDatabase database,
        Guid userId,
        Guid seriesId,
        Guid episodeId,
        DateTimeOffset startedAt,
        CancellationToken ct = default)
    {
        var actor = new TestCurrentUser { Id = userId };
        var run = await new AutopilotRunBuilder(database, actor).CreateAsync(
            userId,
            seriesId,
            slotAt: startedAt,
            startAt: startedAt,
            now: startedAt,
            episodeId: episodeId,
            ct: ct);

        return run.Id;
    }

    /// <summary>
    /// Seeds one run of one series, with everything an evaluation reads: the price version, the owner, the series,
    /// the episode, its current and second story version, the past episode of the corpus and the run row.
    /// </summary>
    /// <param name="database">The class's own database.</param>
    /// <param name="topic">The series topic; <c>true_crime</c> adds checks 14 and 15 and the hold.</param>
    /// <param name="status">The episode status; <c>story_review</c> unless a test says otherwise.</param>
    /// <param name="auditReport">The stored <c>audit_report</c>; <see cref="CleanAudit"/> unless a test varies it.</param>
    /// <param name="similarityScore">The stored source similarity; 0.02 clears the default 0.05 ceiling.</param>
    /// <param name="withPastEpisode">Seeds the series' past episode, so check 10 is scorable.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task<GateWorld> SeedRunAsync(
        TestDatabase database,
        Topic topic = Topic.Ghost,
        EpisodeStatus status = EpisodeStatus.StoryReview,
        string? auditReport = null,
        decimal? similarityScore = 0.02m,
        bool withPastEpisode = true,
        CancellationToken ct = default)
    {
        await SeedPriceVersionAsync(database, ct);

        var actor = new TestCurrentUser();
        var user = await new UserBuilder(database, actor).CreateAsync(ct: ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(database, actor)
            .CreateAsync(user.Id, title: "Gate series", topic: topic, ct: ct);
        var episode = await new EpisodeBuilder(database, actor).CreateAsync(
            user.Id, series.Id, brief: Brief, title: "Gate episode", lengthTier: LengthTier.Short, status: status, ct: ct);

        var pastEpisodeId = Guid.Empty;
        var pastVersionId = Guid.Empty;
        if (withPastEpisode)
        {
            var past = await new EpisodeBuilder(database, actor).CreateAsync(
                user.Id, series.Id, brief: "An earlier episode of the series.", title: "Past episode",
                lengthTier: LengthTier.Short, status: EpisodeStatus.Ready, createdAt: Start.AddDays(-7), ct: ct);
            var pastVersion = await AddVersionAsync(database, user.Id, past.Id, version: 1, CorpusDraft(), Start.AddDays(-7), ct);

            pastEpisodeId = past.Id;
            pastVersionId = pastVersion;
            await database.ExecuteAsync(
                $"UPDATE episodes SET current_story_version_id = '{pastVersion}' WHERE id = '{past.Id}'", ct);
        }

        var versionId = await AddVersionAsync(
            database, user.Id, episode.Id, version: 1, Draft(), Start, ct, auditReport ?? CleanAudit(), similarityScore);
        var secondVersionId = await AddVersionAsync(
            database, user.Id, episode.Id, version: 2, Draft(), Start.AddMinutes(30), ct, auditReport ?? CleanAudit(), similarityScore);

        await database.ExecuteAsync(
            $"UPDATE episodes SET current_story_version_id = '{versionId}' WHERE id = '{episode.Id}'", ct);

        var runId = await SeedRunRowAsync(database, user.Id, series.Id, episode.Id, Start, ct);

        return new GateWorld(
            user.Id, series.Id, episode.Id, versionId, secondVersionId, runId, pastEpisodeId, pastVersionId);
    }

    /// <summary>
    /// Seeds one more run of an existing series, with its own episode and current story version: the second run of
    /// the streak scenario (02.8.3-T4 reads two consecutive <c>gate_failed</c> runs of one series).
    /// </summary>
    /// <param name="database">The class's own database.</param>
    /// <param name="world">The world whose series and owner the new run belongs to.</param>
    /// <param name="auditReport">The stored audit of the new episode's version.</param>
    /// <param name="startedAt">The new run's slot instant, which orders the streak.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task<(Guid EpisodeId, Guid VersionId, Guid RunId)> SeedAdditionalRunAsync(
        TestDatabase database,
        GateWorld world,
        string auditReport,
        DateTimeOffset startedAt,
        CancellationToken ct = default)
    {
        var actor = new TestCurrentUser { Id = world.UserId };
        var episode = await new EpisodeBuilder(database, actor).CreateAsync(
            world.UserId, world.SeriesId, brief: Brief, title: "Second gate episode",
            lengthTier: LengthTier.Short, status: EpisodeStatus.StoryReview, createdAt: startedAt, ct: ct);
        var versionId = await AddVersionAsync(
            database, world.UserId, episode.Id, version: 1, Draft(), startedAt, ct, auditReport, 0.02m);

        await database.ExecuteAsync(
            $"UPDATE episodes SET current_story_version_id = '{versionId}' WHERE id = '{episode.Id}'", ct);

        var runId = await SeedRunRowAsync(database, world.UserId, world.SeriesId, episode.Id, startedAt, ct);

        return (episode.Id, versionId, runId);
    }

    /// <summary>Marks one run terminal the way the run state machine does, so the streak reader can count it.</summary>
    /// <param name="database">The class's own database.</param>
    /// <param name="runId">The run to stop.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static Task MarkRunGateFailedAsync(TestDatabase database, Guid runId, CancellationToken ct = default) =>
        database.ExecuteAsync(
            "UPDATE autopilot_runs SET outcome = 'gate_failed', state = 'gate_failed', "
            + "finished_at = now(), last_transition_at = now() "
            + $"WHERE id = '{runId}'",
            ct);

    /// <summary>The ISO-8601 instant the seed SQL literals carry.</summary>
    internal static string Instant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static async Task<Guid> AddVersionAsync(
        TestDatabase database,
        Guid userId,
        Guid episodeId,
        int version,
        StoryContentV1 draft,
        DateTimeOffset createdAt,
        CancellationToken ct,
        string? auditReport = null,
        decimal? similarityScore = null)
    {
        var row = StoryVersion.Create(
            userId,
            episodeId,
            version,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, new JsonArray()),
            "gate-model",
            "gate-rel-1",
            null,
            null,
            null,
            auditReport,
            similarityScore,
            createdAt);

        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        db.StoryVersions.Add(row);
        await db.SaveChangesAsync(ct);

        return row.Id;
    }

    // A deterministic word run: every call with the same count, seed and lexicon produces the same token count, so
    // the D4 band check reads a known value.
    private static string Words(int count, int seed, string[] lexicon)
    {
        var words = new string[count];
        for (var index = 0; index < count; index++)
        {
            words[index] = lexicon[(seed + index) % lexicon.Length];
        }

        return string.Join(' ', words);
    }

    // The past episode's body shares no word with the gated story's, so check 10 scores ~0 and passes.
    private static StoryContentV1 CorpusDraft() => new(
        "The market behind the orchard",
        new StorySegmentV1(Words(80, 0, PastLexicon), ["market behind the orchard"], [], null),
        [new StorySegmentV1(Words(60, 5, PastLexicon), ["row of copper kettles"], [], "setup")],
        new StorySegmentV1(Words(20, 13, PastLexicon), ["quiet granite meadow"], [], null),
        "An earlier story of the same series.",
        ["slice"],
        [],
        new StoryStructureV1("slice", "orchard", "a kettle", "the trader", "third_person"));

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static readonly string[] MainLexicon =
    [
        "the", "lantern", "keeper", "walked", "along", "quiet", "pier", "night", "wind", "river",
        "stone", "water", "light", "shadow", "morning", "cold", "path", "harbour", "boat", "rope",
        "window", "room", "clock", "table", "letter", "paper", "silent", "slow", "grey", "dawn",
    ];

    private static readonly string[] PastLexicon =
    [
        "sparrow", "market", "bicycle", "engine", "trail", "copper", "tunnel", "orchard", "kettle", "saddle",
        "marble", "feather", "granite", "meadow", "trumpet", "cartwheel", "plum", "fjord", "tundra", "pebble",
        "ripple", "gable", "thistle", "clover",
    ];
}
