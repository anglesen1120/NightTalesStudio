using System.Text.Json.Nodes;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Gates;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// <see cref="GateResultWriter"/> (02.8.2 plan §3.4) against a real <c>AppDbContext</c>: the story and the video
/// verdict of one run merge into one <c>gates.v1</c> document, a lower story attempt never overwrites a higher
/// one, an unreadable stored document degrades to a fresh one instead of failing the gate, and a dry run is what
/// the stored <c>mode</c> says it is.
/// </summary>
/// <remarks>
/// The writer is called directly, with a context pinned to the run's owner (the tenant filter is the caller's,
/// 09 README D10), and the column is read back through SQL as well, so "stored" means the <c>jsonb</c> column and
/// not only the tracked entity.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GateResultWriterTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid VersionId = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    private static readonly Guid RenderJobId = Guid.Parse("018f0000-0000-7000-8000-000000000002");
    private static readonly Guid AssetId = Guid.Parse("018f0000-0000-7000-8000-000000000003");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GateResultWriter_MergesStoryThenVideo_PreservesStoryChecks()
    {
        var world = await SeedAsync();
        await using var db = Open(world, out var clock);
        var writer = new GateResultWriter(db, clock);

        var story = await writer.WriteStoryAsync(
            world.RunId,
            Story(attempt: 1, pass: true),
            Evidence(),
            GateMode.Live,
            "default",
            Ct);
        story.IsError.ShouldBeFalse(story.Errors.Count == 0 ? null : story.FirstError.Description);

        var video = await writer.WriteVideoAsync(world.RunId, Video(), GateMode.Live, "default", Ct);
        video.IsError.ShouldBeFalse();

        await db.SaveChangesAsync(Ct);

        var document = await StoredAsync(world.RunId);

        document["schema_version"]!.GetValue<string>().ShouldBe(GateResultWriter.SchemaVersion);
        document["mode"]!.GetValue<string>().ShouldBe("live");
        document["profile"]!.GetValue<string>().ShouldBe("default");
        document["story"]!["checks"]!.AsArray().Count.ShouldBe(2, "the video write must not touch the story block");
        document["story"]!["story_version_id"]!.GetValue<string>().ShouldBe(VersionId.ToString());
        document["story"]!["episode_status_at_evaluation"]!.GetValue<string>().ShouldBe("story_review");
        document["video"]!["render_job_id"]!.GetValue<string>().ShouldBe(RenderJobId.ToString());
        document["video"]!["asset_id"]!.GetValue<string>().ShouldBe(AssetId.ToString());
        document["video"]!["checks"]!.AsArray().Count.ShouldBe(1);
        document["similarity"]!["corpus_max"]!.GetValue<int>().ShouldBe(50);
        document["real_world"]!["hit"]!.GetValue<bool>().ShouldBeFalse();
        document["moderation"]!["flagged"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task GateResultWriter_SecondStoryAttempt_KeepsHigherAttempt()
    {
        var world = await SeedAsync();
        await using var db = Open(world, out var clock);
        var writer = new GateResultWriter(db, clock);

        var second = await writer.WriteStoryAsync(
            world.RunId,
            Story(attempt: 2, pass: true),
            Evidence(),
            GateMode.Live,
            "default",
            Ct);
        second.IsError.ShouldBeFalse();

        var late = await writer.WriteStoryAsync(
            world.RunId,
            Story(attempt: 1, pass: false),
            Evidence(),
            GateMode.Live,
            "default",
            Ct);
        late.IsError.ShouldBeFalse("a superseded attempt is ignored, not an error");

        var equal = await writer.WriteStoryAsync(
            world.RunId,
            Story(attempt: 2, pass: false),
            Evidence(),
            GateMode.Live,
            "default",
            Ct);
        equal.IsError.ShouldBeFalse();

        await db.SaveChangesAsync(Ct);

        var story = (await StoredAsync(world.RunId))["story"]!.AsObject();
        story["attempt"]!.GetValue<int>().ShouldBe(2);
        story["pass"]!.GetValue<bool>().ShouldBeFalse("the same attempt replaces, the lower one was dropped");
    }

    [Fact]
    public async Task GateResultWriter_GarbageStoredDocument_StartsAFreshDocument()
    {
        var world = await SeedAsync(storedDocument: "[1,2]");
        await using var db = Open(world, out var clock);
        var writer = new GateResultWriter(db, clock);

        var written = await writer.WriteStoryAsync(
            world.RunId,
            Story(attempt: 1, pass: true),
            Evidence(),
            GateMode.DryRun,
            "strict",
            Ct);

        written.IsError.ShouldBeFalse();
        await db.SaveChangesAsync(Ct);

        var document = await StoredAsync(world.RunId);

        document["schema_version"]!.GetValue<string>().ShouldBe(GateResultWriter.SchemaVersion);
        document["mode"]!.GetValue<string>().ShouldBe("dry_run", "the dry run is what the stored row says");
        document["profile"]!.GetValue<string>().ShouldBe("strict");
        document["story"]!["pass"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task GateResultWriter_AppendFailure_AddsTheCodeOnce_AndKeepsTheVerdict()
    {
        var world = await SeedAsync();
        await using var db = Open(world, out var clock);
        var writer = new GateResultWriter(db, clock);

        await writer.WriteStoryAsync(world.RunId, Story(attempt: 1, pass: true), Evidence(), GateMode.Live, "default", Ct);

        var first = await writer.AppendFailureAsync(world.RunId, GateResultWriter.StoryGate, "etag_conflict", Ct);
        first.IsError.ShouldBeFalse();
        first.Value.ShouldBe(GateWriteResult.Written, "the first refusal is one the owner still has to be told about");

        // F8/SEC-9: the same code on the same block is the refusal the run already holds, so the caller must not
        // queue the owner's notice for it a second time.
        var second = await writer.AppendFailureAsync(world.RunId, GateResultWriter.StoryGate, "etag_conflict", Ct);
        second.IsError.ShouldBeFalse();
        second.Value.ShouldBe(GateWriteResult.Unchanged);

        await db.SaveChangesAsync(Ct);

        var story = (await StoredAsync(world.RunId))["story"]!.AsObject();
        story["failures"]!.AsArray().Count.ShouldBe(1);
        story["failures"]![0]!.GetValue<string>().ShouldBe("etag_conflict");
        story["pass"]!.GetValue<bool>().ShouldBeTrue("appending a refusal never rewrites the verdict");
        story["checks"]!.AsArray().Count.ShouldBe(2);
    }

    /// <summary>
    /// F8 at the write itself: the same verdict of the same run and mode is <see cref="GateWriteResult.Unchanged"/>
    /// — that is what stops the second owner notice — while another mode or another attempt is a new verdict the
    /// caller has to notify about.
    /// </summary>
    [Fact]
    public async Task GateResultWriter_ReevaluatedVerdict_ReportsWhetherAnythingChanged()
    {
        var world = await SeedAsync();
        await using var db = Open(world, out var clock);
        var writer = new GateResultWriter(db, clock);

        var first = await writer.WriteStoryAsync(world.RunId, Story(attempt: 1, pass: false), Evidence(), GateMode.Live, "default", Ct);
        first.Value.ShouldBe(GateWriteResult.Written);

        var again = await writer.WriteStoryAsync(world.RunId, Story(attempt: 1, pass: false), Evidence(), GateMode.Live, "default", Ct);
        again.Value.ShouldBe(GateWriteResult.Unchanged, "the tick's retry re-evaluates a stop the run already holds");

        var dryRun = await writer.WriteStoryAsync(world.RunId, Story(attempt: 1, pass: false), Evidence(), GateMode.DryRun, "default", Ct);
        dryRun.Value.ShouldBe(GateWriteResult.Written, "the mode is what turns a verdict into a stop, so it is part of the comparison");

        var nextAttempt = await writer.WriteStoryAsync(world.RunId, Story(attempt: 2, pass: false), Evidence(), GateMode.DryRun, "default", Ct);
        nextAttempt.Value.ShouldBe(GateWriteResult.Written, "a later attempt is a new verdict");

        var lateAttempt = await writer.WriteStoryAsync(world.RunId, Story(attempt: 1, pass: false), Evidence(), GateMode.DryRun, "default", Ct);
        lateAttempt.Value.ShouldBe(GateWriteResult.Superseded, "the stored attempt 2 is never overwritten by a late attempt 1");

        var video = await writer.WriteVideoAsync(world.RunId, Video(), GateMode.Live, "default", Ct);
        video.Value.ShouldBe(GateWriteResult.Written);

        var videoAgain = await writer.WriteVideoAsync(world.RunId, Video(), GateMode.Live, "default", Ct);
        videoAgain.Value.ShouldBe(GateWriteResult.Unchanged);

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// SEC-8: a moderation category PostgreSQL refuses (a NUL, a lone surrogate) is dropped at copy time, so one
    /// unreadable vendor string cannot make the whole verdict — and with it the owner's notice — unstoreable.
    /// </summary>
    [Fact]
    public async Task GateResultWriter_NonStorableModerationCategory_IsDroppedAtCopyTime()
    {
        var world = await SeedAsync();
        await using var db = Open(world, out var clock);
        var writer = new GateResultWriter(db, clock);

        var written = await writer.WriteStoryAsync(
            world.RunId,
            Story(attempt: 1, pass: true),
            Evidence(categories: ["harassment", "self\u0000harm", "unpaired\ud800surrogate"]),
            GateMode.Live,
            "default",
            Ct);

        written.IsError.ShouldBeFalse(written.Errors.Count == 0 ? null : written.FirstError.Description);
        written.Value.ShouldBe(GateWriteResult.Written);

        await db.SaveChangesAsync(Ct);

        (await StoredAsync(world.RunId))["moderation"]!["categories"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ShouldBe(["harassment"], "the storable categories survive in order; the unreadable ones are dropped");
    }

    /// <summary>
    /// SEC-8: when a value the gate has no copy-time filter for (here the moderator's own unavailable detail)
    /// carries what <c>jsonb</c> refuses, the write answers the fixed code and stores nothing instead of letting
    /// the exception at commit fail the verdict's transaction on every retry.
    /// </summary>
    [Fact]
    public async Task GateResultWriter_NonStorableVerdictValue_IsRefusedWithTheFixedCode()
    {
        var world = await SeedAsync();
        await using var db = Open(world, out var clock);
        var writer = new GateResultWriter(db, clock);

        var written = await writer.WriteStoryAsync(
            world.RunId,
            Story(attempt: 1, pass: false),
            Evidence(unavailableDetail: "provider returned\u0000an unreadable body"),
            GateMode.Live,
            "default",
            Ct);

        written.IsError.ShouldBeTrue();
        written.FirstError.Code.ShouldBe(GateResultWriter.NotPersistableErrorCode);

        await db.SaveChangesAsync(Ct);

        var nulls = await Database.QueryStringsAsync(
            $"SELECT count(*)::text FROM autopilot_runs WHERE id = '{world.RunId}' AND gate_results IS NULL",
            Ct);

        nulls.Single().ShouldBe("1", "a refused document writes nothing at all");
    }

    /// <summary>SEC-6: the stored value a gate log line carries is bounded and free of control characters.</summary>
    [Fact]
    public void GateResultWriter_LogSafe_BoundsAndStripsAStoredValue()
    {
        GateResultWriter.LogSafe(null).ShouldBeEmpty();
        GateResultWriter.LogSafe("default").ShouldBe("default");

        var hostile = "no-such-profile\t" + new string('x', 300) + "\u0001tail";
        var safe = GateResultWriter.LogSafe(hostile);

        safe.Length.ShouldBe(GateResultWriter.LogSafeMaxLength);
        safe.ShouldStartWith("no-such-profile?" + new string('x', GateResultWriter.LogSafeMaxLength - 16));
        safe.ShouldNotContain("\t");
        safe.ShouldNotContain("\u0001");

        // A cut inside a surrogate pair drops the orphan, so the line never carries half of one.
        var emoji = new string('x', GateResultWriter.LogSafeMaxLength - 1) + "\ud83c\udfac";
        GateResultWriter.LogSafe(emoji).Length.ShouldBe(GateResultWriter.LogSafeMaxLength - 1);
    }

    [Fact]
    public async Task GateResultWriter_MissingRun_IsNotFound()
    {
        var world = await SeedAsync();
        await using var db = Open(world, out var clock);

        var written = await new GateResultWriter(db, clock)
            .WriteStoryAsync(Guid.CreateVersion7(), Story(attempt: 1, pass: true), Evidence(), GateMode.Live, "default", Ct);

        written.IsError.ShouldBeTrue();
        written.FirstError.Type.ShouldBe(ErrorOr.ErrorType.NotFound);
    }

    private static StoryGateResult Story(int attempt, bool pass) => new(
        Pass: pass,
        Hold: false,
        Attempt: attempt,
        StoryVersionId: VersionId,
        Checks:
        [
            new GateCheck(StoryGateCheckIds.SelfReviewMeanScore, GateCheckKinds.Minimum, 3.9m, 3.5m, pass, null),
            new GateCheck(StoryGateCheckIds.TrueCrimeHold, GateCheckKinds.Flag, 0m, null, pass, null),
        ],
        Failures: pass ? [] : [StoryGateCheckIds.SelfReviewMeanScore]);

    private static VideoGateResult Video() => new(
        Pass: true,
        RenderJobId: RenderJobId,
        AssetId: AssetId,
        Checks: [new GateCheck(VideoGateCheckIds.Loudness, GateCheckKinds.Range, -15.1m, -14m, true, "LUFS")],
        Failures: []);

    private static GateStoryEvidence Evidence(
        IReadOnlyList<string>? categories = null,
        string? unavailableDetail = null) => new(
        SourceSimilarity: 0.021m,
        PastEpisodeSimilarity: 0.044m,
        CorpusMax: 50,
        RealWorldHit: false,
        RealWorldKinds: [],
        ModerationFlagged: false,
        ModerationCategories: categories ?? [],
        ModerationUnavailableDetail: unavailableDetail,
        ModerationCheckedAt: null,
        EpisodeStatusAtEvaluation: "story_review");

    private AppDbContext Open(World world, out IClock clock)
    {
        clock = new FixedClock(Start);

        return Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, clock);
    }

    private async Task<JsonObject> StoredAsync(Guid runId)
    {
        var stored = await Database.QueryStringsAsync(
            $"SELECT gate_results::text FROM autopilot_runs WHERE id = '{runId}'");

        stored.Count.ShouldBe(1);

        return JsonNode.Parse(stored[0])!.AsObject();
    }

    private async Task<World> SeedAsync(string? storedDocument = null)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: CancellationToken.None);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, ct: CancellationToken.None);
        var runId = Guid.CreateVersion7();
        var stored = storedDocument is null ? "NULL" : $"'{storedDocument}'::jsonb";

        await Database.ExecuteAsync(
            "INSERT INTO autopilot_runs (id, user_id, series_id, episode_id, started_at, gate_results, created_at, updated_at) "
            + $"VALUES ('{runId}', '{user.Id}', '{series.Id}', '{episode.Id}', '{Start:O}', {stored}, now(), now())");

        return new World(user.Id, runId, episode.Id);
    }

    private sealed record World(Guid UserId, Guid RunId, Guid EpisodeId);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
