using System.Globalization;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Compliance;
using NightTales.TestKit.Database;
using NightTales.Worker.Cli;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>
/// Review fixes of the restore drill (13.3.1-T1): CR-2 (completed history replays before its open successor, and a
/// successor is never taken for the history's twin) and SEC-2 (<c>deletions export</c> creates a new owner-only file).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeletionRestoreReviewTests(TestDatabase database) : IntegrationTest(database)
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
    public async Task Reapply_CompletedHistoryWithOpenFutureSuccessor_KeepsBothIdentities_AndIsIdempotent()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, configure: host => host.WithSetting("DataDeletion:AllowRestoreReapply", "true"), ct: Ct);
        var now = harness.Clock.UtcNow;
        var user = (await Database.CreateUserBuilder().CreateAsync(ct: Ct)).Id;
        var target = Guid.CreateVersion7();
        var historyId = Guid.CreateVersion7();
        var successorId = Guid.CreateVersion7();
        var successorRequested = now - TimeSpan.FromDays(1);

        // Same (user, scope, target, reason): A was requested and completed earlier; B is open and not due yet.
        var file = string.Join(
            '\n',
            Line(successorId, user, "channel", target, "in_app_revoke", successorRequested, null),
            Line(historyId, user, "channel", target, "in_app_revoke", now - TimeSpan.FromDays(9), now - TimeSpan.FromDays(2)));

        var first = await RunAsync(harness, ["deletions", "reapply", "--since", Iso(Since)], file);

        first.Code.ShouldBe(0, first.Err);
        await AssertStateAsync(historyId, successorId, successorRequested);

        var second = await RunAsync(harness, ["deletions", "reapply", "--since", Iso(Since)], file);

        second.Code.ShouldBe(0, second.Err);
        await AssertStateAsync(historyId, successorId, successorRequested);
    }

    [Fact]
    public async Task Reapply_OpenSuccessorAlreadyHeld_IsNotConsumedAsTheHistoricTwin()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, configure: host => host.WithSetting("DataDeletion:AllowRestoreReapply", "true"), ct: Ct);
        var now = harness.Clock.UtcNow;
        var user = (await Database.CreateUserBuilder().CreateAsync(ct: Ct)).Id;
        var target = Guid.CreateVersion7();
        var successorId = await DeletionRequestFactory.InsertAsync(Database, user, DeletionScope.Channel, target, DeletionReason.InAppRevoke, now - TimeSpan.FromDays(1), ct: Ct);
        var historyId = Guid.CreateVersion7();

        var result = await RunAsync(
            harness,
            ["deletions", "reapply", "--since", Iso(Since)],
            Line(historyId, user, "channel", target, "in_app_revoke", now - TimeSpan.FromDays(9), now - TimeSpan.FromDays(2)));

        result.Code.ShouldBe(1, "the history cannot be re-inserted beside an open successor, and it must not complete the successor");
        (await Database.QueryStringsAsync($"SELECT status FROM data_deletion_requests WHERE id = '{successorId}'", Ct)).ShouldBe(["pending"]);
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM data_deletion_requests WHERE id = '{historyId}'", Ct)).ShouldBe(["0"]);
    }

    [Fact]
    public async Task Export_ExistingPath_IsRefusedWithUsageExit_AndLeftUntouched()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var path = Path.Combine(Path.GetTempPath(), $"nts-export-{Guid.NewGuid():N}.ndjson");
        await File.WriteAllTextAsync(path, "keep", Ct);

        try
        {
            var result = await RunAsync(harness, ["deletions", "export", "--since", Iso(Since), "--out", path]);

            result.Code.ShouldBe(2);
            result.Err.ShouldContain("new file");
            (await File.ReadAllTextAsync(path, Ct)).ShouldBe("keep");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Export_SymlinkPath_IsRefusedAndNotFollowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var victim = Path.Combine(Path.GetTempPath(), $"nts-victim-{Guid.NewGuid():N}.txt");
        var link = Path.Combine(Path.GetTempPath(), $"nts-link-{Guid.NewGuid():N}.ndjson");
        await File.WriteAllTextAsync(victim, "keep", Ct);
        File.CreateSymbolicLink(link, victim);

        try
        {
            var result = await RunAsync(harness, ["deletions", "export", "--since", Iso(Since), "--out", link]);

            result.Code.ShouldBe(2);
            (await File.ReadAllTextAsync(victim, Ct)).ShouldBe("keep");
        }
        finally
        {
            File.Delete(link);
            File.Delete(victim);
        }
    }

    [Fact]
    public async Task Export_NewFile_IsOwnerReadWriteOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var path = Path.Combine(Path.GetTempPath(), $"nts-export-{Guid.NewGuid():N}.ndjson");

        try
        {
            var result = await RunAsync(harness, ["deletions", "export", "--since", Iso(Since), "--out", path]);

            result.Code.ShouldBe(0, result.Err);
            File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task AssertStateAsync(Guid historyId, Guid successorId, DateTimeOffset successorRequested)
    {
        (await Database.QueryStringsAsync($"SELECT status FROM data_deletion_requests WHERE id = '{historyId}'", Ct)).ShouldBe(["completed"], "the history completes under its own id");
        (await Database.QueryStringsAsync($"SELECT status FROM data_deletion_requests WHERE id = '{successorId}'", Ct)).ShouldBe(["pending"], "the successor stays pending");
        (await Database.QueryStringsAsync($"SELECT to_char(due_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM data_deletion_requests WHERE id = '{successorId}'", Ct))
            .ShouldBe([(successorRequested + TimeSpan.FromDays(7)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)], "the successor keeps its own deadline");
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests", Ct)).ShouldBe(["2"]);
    }
}
