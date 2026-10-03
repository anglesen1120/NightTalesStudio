using System.Globalization;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Compliance;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using NightTales.Worker.Cli;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>
/// <c>nts-worker deletions export|reapply</c> (13.3.1-T1 plan §5.3, 13 README YR12) against real PostgreSQL 17: both
/// halves of the export, the replay of completed and open rows, twice-run idempotency and the loud failures.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeletionsCliTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Since = new(2025, 12, 20, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string Line(Guid id, Guid user, string scope, Guid? target, string reason, DateTimeOffset requested, DateTimeOffset? completed) =>
        $$"""{"id":"{{id}}","user_id":"{{user}}","scope":"{{scope}}","target_id":{{(target is null ? "null" : $"\"{target}\"")}},"reason":"{{reason}}","requested_at":"{{Iso(requested)}}","due_at":"{{Iso(requested + (reason == "google_revoke" ? TimeSpan.FromDays(30) : TimeSpan.FromDays(7)))}}","completed_at":{{(completed is null ? "null" : $"\"{Iso(completed.Value)}\"")}}}""";

    private static async Task<(int Code, string Out, string Err)> RunAsync(DeletionHarness harness, string[] args, string? input = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await DeletionsCommand.RunAsync(args, harness.Host.Services, Ct, new StringReader(input ?? string.Empty), output, error);

        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Cli_ExportIncludesCompletedAndStillOpenRows()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var now = harness.Clock.UtcNow;
        var before = await DeletionRequestFactory.InsertAsync(Database, Guid.CreateVersion7(), DeletionScope.Channel, Guid.CreateVersion7(), DeletionReason.InAppRevoke, Since.AddDays(-10), status: DeletionStatus.Completed, completedAt: Since.AddDays(-2), ct: Ct);
        var after = await DeletionRequestFactory.InsertAsync(Database, Guid.CreateVersion7(), DeletionScope.Channel, Guid.CreateVersion7(), DeletionReason.InAppRevoke, Since.AddDays(-1), status: DeletionStatus.Completed, completedAt: Since.AddDays(3), ct: Ct);
        var open = await DeletionRequestFactory.InsertAsync(Database, Guid.CreateVersion7(), DeletionScope.Account, null, DeletionReason.AccountDelete, now, ct: Ct);
        var path = Path.Combine(Path.GetTempPath(), $"nts-export-{Guid.NewGuid():N}.ndjson");

        try
        {
            var result = await RunAsync(harness, ["deletions", "export", "--since", Iso(Since), "--out", path]);

            result.Code.ShouldBe(0, result.Err);
            result.Out.ShouldContain("exported 2");
            var lines = await File.ReadAllLinesAsync(path, Ct);
            lines.Length.ShouldBe(2);
            lines[0].ShouldContain(open.ToString(), customMessage: "still-open rows come first (completed_at NULLS FIRST)");
            lines[1].ShouldContain(after.ToString());
            string.Join('\n', lines).ShouldNotContain(before.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cli_Reapply_RefusesAHostThatIsNotAScratchInstance()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);

        var result = await RunAsync(harness, ["deletions", "reapply", "--since", Iso(Since)], Line(Guid.CreateVersion7(), Guid.CreateVersion7(), "account", null, "account_delete", Since, null));

        result.Code.ShouldBe(2);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests", Ct)).ShouldBe(["0"]);
    }

    [Fact]
    public async Task Cli_Reapply_CompletesCompletedRows_ReinsertsOpenOnes_AndIsIdempotentTwice()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, configure: host => host.WithSetting("DataDeletion:AllowRestoreReapply", "true"), ct: Ct);
        var now = harness.Clock.UtcNow;
        var user = (await Database.CreateUserBuilder().CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = user };
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user, ct: Ct);
        var episodes = new EpisodeBuilder(Database, actor);
        var episode = await episodes.CreateAsync(user, series.Id, ct: Ct);
        await episodes.CreateAssetAsync(user, episode.Id, ct: Ct);
        var key = (await Database.QueryStringsAsync($"SELECT storage_key FROM assets WHERE episode_id = '{episode.Id}'", Ct)).Single();
        harness.Blobs.Seed("nt-media-dev", key, [1]);

        // The restored database lacks all three rows: the completed episode deletion, an open channel duty that is due,
        // and an open channel duty that is not due yet.
        var completedId = Guid.CreateVersion7();
        var dueId = Guid.CreateVersion7();
        var futureId = Guid.CreateVersion7();
        var file = string.Join(
            '\n',
            Line(completedId, user, "episode", episode.Id, "user_request", now - TimeSpan.FromDays(9), now - TimeSpan.FromDays(2)),
            Line(dueId, user, "channel", Guid.CreateVersion7(), "in_app_revoke", now - TimeSpan.FromDays(8), null),
            Line(futureId, user, "channel", Guid.CreateVersion7(), "in_app_revoke", now - TimeSpan.FromDays(1), null));

        var first = await RunAsync(harness, ["deletions", "reapply", "--since", Iso(Since)], file);

        first.Code.ShouldBe(0, first.Err);
        harness.Blobs.Contains("nt-media-dev", key).ShouldBeFalse();
        (await Database.QueryStringsAsync($"SELECT status FROM data_deletion_requests WHERE id = '{completedId}'", Ct)).ShouldBe(["completed"]);
        (await Database.QueryStringsAsync($"SELECT status FROM data_deletion_requests WHERE id = '{dueId}'", Ct)).ShouldBe(["completed"]);
        (await Database.QueryStringsAsync($"SELECT status || ':' || coalesce(reapplied_at::text, '-') FROM data_deletion_requests WHERE id = '{futureId}'", Ct))
            .Single().ShouldStartWith("pending:2026");
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM audit_logs WHERE action = 'account.data_deleted' AND visibility = 'internal' AND data ->> 'actor' = 'system:operator'", Ct))
            .ShouldBe(["2"]);

        var deletes = harness.Blobs.CountOf(BlobOperation.Delete);
        var rows = await Database.QueryStringsAsync("SELECT id || status FROM data_deletion_requests ORDER BY id", Ct);

        var second = await RunAsync(harness, ["deletions", "reapply", "--since", Iso(Since)], file);

        second.Code.ShouldBe(0, second.Err);
        harness.Blobs.CountOf(BlobOperation.Delete).ShouldBe(deletes, "the second run deletes nothing further");
        (await Database.QueryStringsAsync("SELECT id || status FROM data_deletion_requests ORDER BY id", Ct)).ShouldBe(rows, "same resulting state");
    }

    [Fact]
    public async Task Cli_Reapply_UnregisteredScopeUnparseableOrEmptyResult_ExitsNonZero()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, configure: host => host.WithSetting("DataDeletion:AllowRestoreReapply", "true"), ct: Ct);
        var now = harness.Clock.UtcNow;
        var user = Guid.CreateVersion7();

        // `account` is deferred in this host: no handler, so the row cannot be applied.
        var unregistered = await RunAsync(harness, ["deletions", "reapply", "--since", Iso(Since)],
            Line(Guid.CreateVersion7(), user, "account", null, "account_delete", now - TimeSpan.FromDays(9), now - TimeSpan.FromDays(2)));
        unregistered.Code.ShouldBe(1);
        unregistered.Err.ShouldContain("no handler");

        (await RunAsync(harness, ["deletions", "reapply", "--since", Iso(Since)], "not json")).Code.ShouldBe(1);

        // A non-empty file whose every row predates the restore point applies nothing: never exit 0.
        var nothing = await RunAsync(harness, ["deletions", "reapply", "--since", Iso(Since)],
            Line(Guid.CreateVersion7(), user, "channel", Guid.CreateVersion7(), "in_app_revoke", Since.AddDays(-9), Since.AddDays(-2)));
        nothing.Code.ShouldBe(1);

        (await RunAsync(harness, ["deletions", "bogus"])).Code.ShouldBe(2);
    }
}
