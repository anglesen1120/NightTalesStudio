using System.Net;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Research;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// The body-validation risk list of <c>PUT /api/v1/episodes/{id}/fact-sheet</c> (02.5.4-T1, T51): every
/// rejection answers 400 <c>fact_sheet_invalid_content</c> with the offending <c>facts[i].…</c> field, the
/// <c>If-Match</c> forms that are not the current tag answer 412 <c>fact_sheet_conflict</c>, and the problem
/// carries its code (and a non-empty title) for both <c>Accept-Language</c> values the SPA offers.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactSheetPutValidationTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The longest statement a version stores, after normalisation.</summary>
    private const int StatementMaxChars = 600;

    /// <summary>The longest raw statement the edit path scans at all (four times the stored cap).</summary>
    private const int RawStatementMaxChars = StatementMaxChars * 4;

    [Fact]
    public async Task PutFactSheet_InvalidBody_Returns400FactSheetInvalidContent()
    {
        var (world, source, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "   ", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.FieldKeys(await FactSheetHarness.ProblemAsync(response))
            .ShouldContain("facts[0].statement", "the field error names the offending fact");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1, "a rejected body writes no version");
    }

    [Fact]
    public async Task PutFactSheet_MissingBody_Returns400FactSheetInvalidContentWithTheBodyField()
    {
        // The route declares a nullable body, so a request that carries no document is answered here, with the
        // body field, in the same 400 shape as a fact that fails the statement rules.
        var (world, _, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            string.Empty);

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.FieldKeys(await FactSheetHarness.ProblemAsync(response))
            .ShouldContain("body", "a request with no document names the body, not a fact");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateFactSheet_UnknownFactId_Returns400()
    {
        var (world, source, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f9", "A fact with a foreign id.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.FieldKeys(await FactSheetHarness.ProblemAsync(response)).ShouldContain("facts[0].id");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateFactSheet_DuplicateIdInBody_Returns400()
    {
        var (world, source, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(
                FactSheetHarness.Fact("f1", "One fact.", [source]),
                FactSheetHarness.Fact("f1", "The same fact cited twice.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.FieldKeys(await FactSheetHarness.ProblemAsync(response)).ShouldContain("facts[1].id");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateFactSheet_UnknownSourceId_Returns400()
    {
        var (world, _, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "A fact citing an unknown source.", [Guid.CreateVersion7()])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.FieldKeys(await FactSheetHarness.ProblemAsync(response))
            .ShouldContain(key => key.StartsWith("facts[", StringComparison.Ordinal), "the field error names the offending fact");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateFactSheet_AnotherTenantsSourceId_Returns400()
    {
        var (world, _, sheet) = await SeedOneFactAsync();
        var stranger = await FactSheetHarness.SeedAsync(Database);
        var foreignSource = await FactSheetHarness.AddFetchedUrlAsync(Database, stranger);

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "A fact citing another tenant's source.", [foreignSource])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateFactSheet_FactWithoutSources_Returns400()
    {
        var (world, _, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "A fact with no source at all.", [])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PutFactSheet_DeletedSourceStillCited_Returns400()
    {
        // Failure handling: a fact whose only source was removed from the episode is rejected (unknown source id),
        // so the UI can offer to remove that fact instead of saving a citation that resolves to nothing.
        var (world, source, sheet) = await SeedOneFactAsync();
        SqlProbe.Execute(Database, $"DELETE FROM story_sources WHERE id = '{source}'");

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
    }

    [Fact]
    public async Task PutFactSheet_Over40Facts_Returns400()
    {
        var (world, source, sheet) = await SeedOneFactAsync();
        var facts = Enumerable.Range(1, 41)
            .Select(number => FactSheetHarness.Fact(null, $"Statement number {number}.", [source]))
            .ToArray();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(facts));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PutFactSheet_MoreThan10SourceIdsOnOneFact_Returns400()
    {
        var (world, _, sheet) = await SeedOneFactAsync();
        var sourceIds = new List<Guid>();
        // MarkFetched only stores a 64-character lowercase hex content hash, so a fresh source takes a hex letter.
        const string HexDigits = "0123456789abcdef";
        for (var index = 0; index < 11; index++)
        {
            sourceIds.Add(await FactSheetHarness.AddFetchedUrlAsync(Database, world, FactSheetHarness.Sha(HexDigits[index])));
        }

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact(null, "A fact citing eleven sources.", sourceIds)));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PutFactSheet_DuplicateSourceIdInOneFact_Returns400()
    {
        var (world, source, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact(null, "A fact citing one source twice.", [source, source])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("over-statement-cap")]
    [InlineData("over-raw-cap")]
    [InlineData("nul")]
    [InlineData("c0")]
    [InlineData("del")]
    [InlineData("bidi-override")]
    [InlineData("bidi-isolate")]
    public async Task PutFactSheet_RejectedStatement_Returns400WithFieldError(string kind)
    {
        var (world, source, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact(null, RejectedStatement(kind), [source])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        FactSheetHarness.FieldKeys(await FactSheetHarness.ProblemAsync(response)).ShouldContain("facts[0].statement");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PutFactSheet_RejectedStatement_NeverEchoesTheRawStatement()
    {
        // The problem names the field, never the text: a statement carrying a NUL or a bidi override must not be
        // written into a body the SPA renders.
        var (world, source, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact(null, "SECRET-CANARY\u0000 statement.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 400, "fact_sheet_invalid_content");
        (await response.Content.ReadAsStringAsync(FactSheetHarness.Ct)).ShouldNotContain("SECRET-CANARY");
    }

    [Theory]
    [InlineData("W/\"fs-{0}\"")]
    [InlineData("*")]
    [InlineData("\"fs-{0}\", \"fs-{1}\"")]
    [InlineData("\"sv-{0}\"")]
    [InlineData("fs-{0}")]
    [InlineData("\"fs-not-a-guid\"")]
    [InlineData("\"fs-{0}")]
    public async Task PutFactSheet_NonMatchingIfMatchVariant_Returns412(string template)
    {
        var (world, source, sheet) = await SeedOneFactAsync();
        var tag = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            template,
            sheet.Id,
            Guid.CreateVersion7());

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var response = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            tag,
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source])));

        await FactSheetHarness.AssertProblemAsync(response, 412, "fact_sheet_conflict");
        FactSheetHarness.SheetRows(Database, world).Count.ShouldBe(1, "only the exact current tag may save");
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public async Task PutFactSheet_ConflictAndInvalidContent_CarryTheirCodeForEachLanguage(string language)
    {
        // There is no server-side localisation of problem details yet (titles are the Error description and the
        // SPA catalogue in web/src/app/core/errors/error-code.ts owns the vi/en copy), so the contract's fallback
        // applies: the code is exact and the title is present for both languages the SPA sends.
        var (world, source, sheet) = await SeedOneFactAsync();

        using var factory = FactSheetHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var conflict = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(Guid.CreateVersion7()),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "One fact.", [source])),
            language);
        conflict.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var conflictProblem = await FactSheetHarness.ProblemAsync(conflict);
        conflictProblem.Code.ShouldBe("fact_sheet_conflict");
        conflictProblem.Title.ShouldNotBeNullOrWhiteSpace();

        using var invalid = await FactSheetHarness.PutAsync(
            client,
            world.EpisodeId,
            FactSheetHarness.Etag(sheet.Id),
            FactSheetHarness.Body(FactSheetHarness.Fact("f1", "   ", [source])),
            language);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var invalidProblem = await FactSheetHarness.ProblemAsync(invalid);
        invalidProblem.Code.ShouldBe("fact_sheet_invalid_content");
        invalidProblem.Title.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The statement shape one rejected case sends. A lone surrogate has no case here: the wire cannot carry one.
    /// <c>System.Text.Json</c> substitutes U+FFFD when it writes a lone surrogate, and it refuses an unpaired
    /// <c>\ud800</c> escape when it reads one, so no client can make the edit path see the character at all (a
    /// raw escape is a body-level 400 before the statement rules run). The rule itself is pinned where it is
    /// reachable — <c>FactStatementRulesTests.TryNormalize_LoneSurrogate_ReturnsStatementInvalid</c> and
    /// <c>FactSheetValidatorTests.FactSheetValidator_DropsLoneSurrogate</c>.
    /// </summary>
    private static string RejectedStatement(string kind) => kind switch
    {
        "empty" => string.Empty,
        "whitespace" => " \t\n ",
        "over-statement-cap" => new string('x', StatementMaxChars + 1),
        "over-raw-cap" => new string('x', RawStatementMaxChars + 1),
        "nul" => "A fact with a NUL\u0000 in it.",
        "c0" => "A fact with a C0 control\u0001 in it.",
        "del" => "A fact with a DEL\u007F in it.",
        "bidi-override" => "A fact with a bidi override\u202E in it.",
        "bidi-isolate" => "A fact with a bidi isolate\u2066 in it.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown rejected statement kind."),
    };

    /// <summary>Seeds one world with one fetched url source and a one-fact draft.</summary>
    private async Task<(World World, Guid Source, FactSheet Sheet)> SeedOneFactAsync()
    {
        var world = await FactSheetHarness.SeedAsync(Database);
        var source = await FactSheetHarness.AddFetchedUrlAsync(Database, world);
        var sheet = await FactSheetHarness.SeedSheetAsync(Database, world, 1, [FactSheetHarness.Seed("f1", "One fact.", [source])]);

        return (world, source, sheet);
    }
}
