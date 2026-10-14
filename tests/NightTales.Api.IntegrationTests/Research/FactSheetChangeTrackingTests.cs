using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// The D9 "a row is written once" invariant of a stored fact-sheet row, under the real host and a real PostgreSQL
/// 17 (02.5.4-T1): a tracked, untouched sheet is <see cref="EntityState.Unchanged"/> (so no save rewrites its
/// <c>facts</c> jsonb, which the column's own bytes hash), while a real document edit still is a change; and an
/// edit or an approve leaves every row it was not addressing byte-identical, the one legitimate exception being
/// the status flip of the row an approve demotes to <see cref="FactSheetStatus.Superseded"/>.
/// </summary>
/// <remarks>
/// <c>xmin</c> is part of every row snapshot because PostgreSQL gives an updated row a new transaction id even
/// when the new values are the same, so a redundant UPDATE is visible as an <c>xmin</c> move while the row text
/// stands still.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FactSheetChangeTrackingTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task LoadedFactSheet_WithNoEdit_IsNotSeenAsModified()
    {
        // FactItem is a record whose two members are lists, and the record's own equality compares them by
        // reference: without an element-wise ValueComparer every tracked, untouched row would look
        // Facts-modified and every save would rewrite the jsonb column and bump updated_at.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source1 = await FactSheetHarness.AddFetchedUrlAsync(Database, world, sha: FactSheetHarness.Sha('a'));
        var source2 = await FactSheetHarness.AddFetchedUrlAsync(Database, world, sha: FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, Seeds(source1, source2));

        await using var context = TrackedContext(world);
        var tracked = await context.FactSheets.SingleAsync(row => row.Id == sheet.Id, FactSheetHarness.Ct);

        context.ChangeTracker.DetectChanges();

        var entry = context.Entry(tracked);
        entry.State.ShouldBe(EntityState.Unchanged, "a row nobody edited is not a change");
        entry.Property(row => row.Facts)
            .IsModified.ShouldBeFalse("the facts comparer must compare both FactItem lists by value");
        entry.Property(row => row.SourceAliases).IsModified.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadedFactSheet_WithOneChangedStatement_IsSeenAsModified()
    {
        // The positive control of the case above: the very same comparer must still see a real edit of the
        // document, so the Unchanged assertion is not a comparer that answers "equal" to everything.
        var world = await FactSheetHarness.SeedAsync(Database);
        var source1 = await FactSheetHarness.AddFetchedUrlAsync(Database, world, sha: FactSheetHarness.Sha('a'));
        var source2 = await FactSheetHarness.AddFetchedUrlAsync(Database, world, sha: FactSheetHarness.Sha('b'));
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, Seeds(source1, source2));

        await using var context = TrackedContext(world);
        var tracked = await context.FactSheets.SingleAsync(row => row.Id == sheet.Id, FactSheetHarness.Ct);

        // The aggregate exposes no statement mutator (D9: a stored row is never edited), so the edit goes
        // through the very property the serializer maps — the same path the change tracker reads.
        var changed = new List<FactItem>(tracked.Facts);
        changed[0] = changed[0] with { Statement = "Fact one, corrected." };
        context.Entry(tracked).Property(row => row.Facts).CurrentValue = changed;

        context.ChangeTracker.DetectChanges();

        var entry = context.Entry(tracked);
        entry.State.ShouldBe(EntityState.Modified);
        entry.Property(row => row.Facts).IsModified.ShouldBeTrue();
    }

    [Fact]
    public async Task PutAndApprove_LeaveEveryRowTheyWereNotAddressingByteIdentical()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source1 = await FactSheetHarness.AddFetchedUrlAsync(Database, world, sha: FactSheetHarness.Sha('a'));
        var source2 = await FactSheetHarness.AddFetchedUrlAsync(Database, world, sha: FactSheetHarness.Sha('b'));
        var version1 = await FactSheetHarness.SeedSheetAsync(Database, world, 1, Seeds(source1, source2));
        var version1Before = RowBytes(version1.Id);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // (1) An edit adds a version and must not rewrite the row it was based on (D9), not even its jsonb.
        using var edit = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(version1.Id),
            EditBody(source1, source2, "Fact one, corrected."));
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        var version2Id = FactSheetHarness.CurrentSheetId(Database, world);
        version2Id.ShouldNotBe(version1.Id);
        RowBytes(version1.Id).ShouldBe(version1Before, "an edit writes a new row and never touches the edited one");

        // (2) Approving the draft moves only that row's status and stamps it.
        using var approve = await FactSheetHarness.ApproveAsync(client, world.EpisodeId, FactSheetHarness.Etag(version2Id));
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync(FactSheetHarness.Ct));
        RowBytes(version1.Id).ShouldBe(version1Before);
        var version2Approved = RowBytes(version2Id);
        var version2Facts = FactsJson(version2Id);

        // (3) Editing the approved row makes the next version and still does not touch version 2.
        using var secondEdit = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(version2Id),
            EditBody(source1, source2, "Fact one, corrected twice."));
        secondEdit.StatusCode.ShouldBe(HttpStatusCode.OK, await secondEdit.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        var version3Id = FactSheetHarness.CurrentSheetId(Database, world);
        version3Id.ShouldNotBe(version2Id);
        RowBytes(version2Id).ShouldBe(version2Approved, "an edit never touches the approved row it was based on");
        RowBytes(version1.Id).ShouldBe(version1Before);

        // (4) Approving version 3 supersedes version 2: that row's status legitimately moves, its bytes may not.
        using var secondApprove = await FactSheetHarness.ApproveAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(version3Id));
        secondApprove.StatusCode.ShouldBe(HttpStatusCode.OK, await secondApprove.Content.ReadAsStringAsync(FactSheetHarness.Ct));

        Status(version2Id).ShouldBe(SupersededStatus, "the demoted row is the intended change");
        FactsJson(version2Id).ShouldBe(version2Facts, "content_sha256 hashes the stored bytes, so they never move");
        RowBytes(version2Id).ShouldNotBe(version2Approved, "the demotion is a real update of that row");
        RowBytes(version1.Id).ShouldBe(version1Before);
    }

    /// <summary>A write context the test owns, over this class's database and the tenant that owns the world.</summary>
    private AppDbContext TrackedContext(World world) =>
        Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }, new FakeClock(FactSheetHarness.Start));

    /// <summary>
    /// A row's identity and mutable columns (<c>xmin</c>, <c>status</c>, <c>updated_at</c>): an UPDATE replaces
    /// the row version, so <c>xmin</c> moves even when the written values are unchanged.
    /// </summary>
    private string RowBytes(Guid sheetId) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT xmin::text || '|' || status::text || '|' || updated_at::text "
            + $"FROM fact_sheets WHERE id = '{sheetId}'");

    /// <summary>The stored <c>facts</c> document as text: the bytes <c>content_sha256</c> hashes.</summary>
    private string FactsJson(Guid sheetId) =>
        SqlProbe.ScalarText(Database, $"SELECT facts::text FROM fact_sheets WHERE id = '{sheetId}'");

    /// <summary>The stored status of one row as its smallint text.</summary>
    private string Status(Guid sheetId) =>
        SqlProbe.ScalarText(Database, $"SELECT status::text FROM fact_sheets WHERE id = '{sheetId}'");

    /// <summary>The smallint of <see cref="FactSheetStatus.Superseded"/> as the <c>status::text</c> probe renders it.</summary>
    private static readonly string SupersededStatus =
        ((short)FactSheetStatus.Superseded).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Five valid facts over the two sources: enough to pass the approve-side minimum (<c>MinFacts</c>) and to
    /// cite both fetched url sources with distinct content hashes (F13).
    /// </summary>
    private static FactSeed[] Seeds(Guid source1, Guid source2) =>
    [
        FactSheetHarness.Seed("f1", "Fact one.", [source1, source2]),
        FactSheetHarness.Seed("f2", "Fact two.", [source1]),
        FactSheetHarness.Seed("f3", "Fact three.", [source2]),
        FactSheetHarness.Seed("f4", "Fact four.", [source1, source2]),
        FactSheetHarness.Seed("f5", "Fact five.", [source1]),
    ];

    /// <summary>The same five facts as a PUT body, with <paramref name="first"/> as the first statement.</summary>
    private static string EditBody(Guid source1, Guid source2, string first) =>
        FactSheetHarness.Body(
            FactSheetHarness.Fact("f1", first, [source1, source2]),
            FactSheetHarness.Fact("f2", "Fact two.", [source1]),
            FactSheetHarness.Fact("f3", "Fact three.", [source2]),
            FactSheetHarness.Fact("f4", "Fact four.", [source1, source2]),
            FactSheetHarness.Fact("f5", "Fact five.", [source1]));
}
