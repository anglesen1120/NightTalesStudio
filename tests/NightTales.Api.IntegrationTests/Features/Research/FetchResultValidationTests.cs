using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Storage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// The body rules of the result route (02.5.2-T2 Design items 2-4, T39) through the real host: every refusal is a 400 with
/// its D8 code (or <c>validation_failed</c> for a body the schema refuses), leaves the task <c>leased</c> and the source
/// <c>pending</c>, and writes no object. The cases are the defect classes of an input-parsing endpoint: caps, hash
/// corruption, NUL / C0 / lone surrogates, a missing, unknown or numeric enum, and a body that is not an object.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FetchResultValidationTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    private ApiFactory NewFactory(InMemoryBlobStore blobs) =>
        new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start))
            .WithFetcherInternalApi()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(blobs);
            });

    private async Task<(FetchTaskSeed Seed, FetchTaskSeed.World World, FetchTaskSeed.SeededTask Task)> SeedLeasedAsync()
    {
        var seed = new FetchTaskSeed(Database);
        seed.ResetTasks();
        var world = await seed.SeedWorldAsync();

        return (seed, world, seed.AddLeased(world, "validation.example"));
    }

    /// <summary>Posts <paramref name="body"/> and asserts the refusal left no trace.</summary>
    private async Task<HttpResponseMessage> PostRefusedAsync(object body, HttpStatusCode expected, string? expectedCode)
    {
        var (seed, world, task) = await SeedLeasedAsync();
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), body);

        await AssertRefusalAsync(response, expected, expectedCode, seed, world, task, blobs);

        return response;
    }

    private async Task AssertRawRefusedAsync(string json, HttpStatusCode expected, string? expectedCode)
    {
        var (seed, world, task) = await SeedLeasedAsync();
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), json);

        await AssertRefusalAsync(response, expected, expectedCode, seed, world, task, blobs);
    }

    private static async Task AssertRefusalAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string? expectedCode,
        FetchTaskSeed seed,
        FetchTaskSeed.World world,
        FetchTaskSeed.SeededTask task,
        InMemoryBlobStore blobs)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text.Length > 400 ? text[..400] : text);
        if (expectedCode is not null)
        {
            (await FetchTaskSeed.CodeOfAsync(response)).ShouldBe(expectedCode);
        }

        FetchTaskSeed.ResearchCalls(blobs).ShouldBeEmpty("a refused body writes no object");
        seed.SourceStatus(task.SourceId).ShouldBe("pending");
        seed.TaskStatus(task.TaskId).ShouldBe("leased", "a 400 leaves the task leased; the sweeper decides");
        seed.Count($"SELECT count(*) FROM outbox_messages WHERE type = 'source_fetched.v1' AND user_id = '{world.UserId}'").ShouldBe(0);
    }

    // ------------------------------------------------------------------ caps

    [Fact]
    public async Task Result_OverMaxChars_Returns400SourceTooLarge()
    {
        var body = FetchTaskSeed.DoneBody(new string('a', 300_001));

        using var response = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_too_large");
    }

    [Fact]
    public async Task Result_OverMaxChars_IsCheckedBeforeTheHash()
    {
        var body = FetchTaskSeed.DoneBody(new string('a', 300_001));
        body["textSha256"] = FetchTaskSeed.Sha256("not the text");

        using var response = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_too_large");
    }

    [Fact]
    public async Task Result_BodyOverOneMebibyte_Returns413()
    {
        var body = FetchTaskSeed.DoneBody(new string('a', 1_048_577));

        using var response = await PostRefusedAsync(body, HttpStatusCode.RequestEntityTooLarge, null);
    }

    // ------------------------------------------------------------------ status

    [Fact]
    public async Task Result_MissingStatus_Returns400()
    {
        var body = FetchTaskSeed.DoneBody();
        body.Remove("status");

        using var response = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "validation_failed");
        (await FetchTaskSeed.ReadJsonAsync(response)).GetProperty("errors").TryGetProperty("status", out _).ShouldBeTrue("the problem names the member");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"ok\"")]
    [InlineData("\"DONE\"")]
    [InlineData("\" done\"")]
    [InlineData("\"done\\u0000\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("[\"done\"]")]
    [InlineData("{\"v\":\"done\"}")]
    public async Task Result_UnknownOrMistypedStatus_Returns400_NeverA500(string statusJson)
    {
        var text = "hello";
        var json = "{\"workerId\":\"fetcher-1\",\"status\":" + statusJson + ",\"finalUrl\":\"https://news.example.com/a\","
            + "\"contentType\":\"text/html\",\"text\":\"" + text + "\",\"textSha256\":\"" + FetchTaskSeed.Sha256(text) + "\"}";

        await AssertRawRefusedAsync(json, HttpStatusCode.BadRequest, "validation_failed");
    }

    // ------------------------------------------------------------------ text

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Result_TextMissingOrEmpty_Returns400SourceNoText(string? text)
    {
        var body = FetchTaskSeed.DoneBody();
        body["text"] = text;
        body["textSha256"] = FetchTaskSeed.Sha256(text ?? string.Empty);

        using var response = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_no_text");
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x08)]
    [InlineData(0x1f)]
    [InlineData(0x7f)]
    public async Task Result_TextWithNulOrControlCharacter_Returns400SourceNoText(int codePoint)
    {
        var body = FetchTaskSeed.DoneBody("before" + (char)codePoint + "after");

        using var response = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_no_text");
    }

    [Theory]
    [InlineData("before\\ud800after")]
    [InlineData("before\\udc00after")]
    [InlineData("before\\ud83dafter")]
    public async Task Result_TextWithLoneSurrogate_Returns400_NeverA500(string escapedText)
    {
        var json = "{\"workerId\":\"fetcher-1\",\"status\":\"done\",\"finalUrl\":\"https://news.example.com/a\",\"contentType\":\"text/html\","
            + "\"text\":\"" + escapedText + "\",\"textSha256\":\"" + FetchTaskSeed.Sha256("anything") + "\"}";

        await AssertRawRefusedAsync(json, HttpStatusCode.BadRequest, null);
    }

    [Fact]
    public async Task Result_TextWithTabNewlineAndCarriageReturn_IsStored()
    {
        var (seed, _, task) = await SeedLeasedAsync();
        var blobs = new InMemoryBlobStore();
        using var factory = NewFactory(blobs);
        using var client = factory.InternalClient();

        using var response = await FetchTaskSeed.PostAsync(
            client,
            FetchTaskSeed.Route(task.TaskId, "result"),
            FetchTaskSeed.DoneBody("a\tb\r\nc\nd"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        seed.SourceStatus(task.SourceId).ShouldBe("fetched");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Result_MissingOrWrongHash_Returns400SourceNoText(string? sha)
    {
        var body = FetchTaskSeed.DoneBody();
        body["textSha256"] = sha;

        using var response = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_no_text");
    }

    [Fact]
    public async Task Result_NumericHashOrText_Returns400_NeverA500()
    {
        await AssertRawRefusedAsync(
            "{\"workerId\":\"fetcher-1\",\"status\":\"done\",\"finalUrl\":\"https://news.example.com/a\",\"contentType\":\"text/html\",\"text\":\"x\",\"textSha256\":12}",
            HttpStatusCode.BadRequest,
            "validation_failed");
    }

    // ------------------------------------------------------------------ final url and content type

    [Theory]
    [InlineData("https://youtube.com/x")]
    [InlineData("https://www.youtube.com/watch?v=1")]
    [InlineData("https://m.youtube.com/watch?v=1")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("https://localhost/x")]
    [InlineData("ftp://news.example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@news.example.com/x")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Result_BlockedOrUnparseableFinalUrl_Returns400SourceDomainNotAllowed(string? finalUrl)
    {
        var body = FetchTaskSeed.DoneBody();
        body["finalUrl"] = finalUrl;

        using var response = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_domain_not_allowed");
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Result_UnsupportedContentType_Returns400SourceUnsupportedType(string? contentType)
    {
        var body = FetchTaskSeed.DoneBody();
        body["contentType"] = contentType;

        using var response = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_unsupported_type");
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("TEXT/HTML; charset=UTF-8")]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    public async Task Result_AcceptedContentType_IsStoredAsTheLowercaseMediaType(string contentType)
    {
        var (seed, _, task) = await SeedLeasedAsync();
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();
        var body = FetchTaskSeed.DoneBody();
        body["contentType"] = contentType;

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        seed.Text($"SELECT content_type FROM story_sources WHERE id = '{task.SourceId}'").ShouldBe(contentType.Split(';')[0].ToLowerInvariant());
    }

    [Fact]
    public async Task Result_CheckOrder_TextThenCapThenHashThenUrlThenType()
    {
        // All five defects at once: the first rule in the documented order wins, then each is removed in turn.
        var body = FetchTaskSeed.DoneBody("text");
        body["textSha256"] = FetchTaskSeed.Sha256("other");
        body["finalUrl"] = "https://youtube.com/x";
        body["contentType"] = "application/pdf";

        using var hash = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_no_text");

        body["textSha256"] = FetchTaskSeed.Sha256("text");
        using var url = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_domain_not_allowed");

        body["finalUrl"] = "https://news.example.com/a";
        using var type = await PostRefusedAsync(body, HttpStatusCode.BadRequest, "source_unsupported_type");
    }

    // ------------------------------------------------------------------ labels and numbers

    [Fact]
    public async Task Result_TitleWithNulAndOverLength_IsCleanedAndCapped()
    {
        var (seed, _, task) = await SeedLeasedAsync();
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();
        var body = FetchTaskSeed.DoneBody();
        body["title"] = "  Tiêu\u0000 đề\u0001 " + new string('x', 600);

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var title = seed.Text($"SELECT title FROM story_sources WHERE id = '{task.SourceId}'");
        title.Length.ShouldBeGreaterThan(0);
        title.Length.ShouldBeLessThanOrEqualTo(300);
        title.ShouldNotContain("\0");
        title.ShouldStartWith("Tiêu", Case.Sensitive);
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("1e400")]
    [InlineData("99999999999")]
    [InlineData("-5")]
    [InlineData("12.5")]
    public async Task Result_HttpStatusOfAnOddShape_IsRejectedOrIgnored_NeverA500(string httpStatusJson)
    {
        var (seed, _, task) = await SeedLeasedAsync();
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();
        var json = "{\"workerId\":\"fetcher-1\",\"status\":\"failed\",\"errorCode\":\"source_unreachable\",\"httpStatus\":" + httpStatusJson + "}";

        using var response = await FetchTaskSeed.PostRawAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), json);

        ((int)response.StatusCode).ShouldBeInRange(200, 499, await response.Content.ReadAsStringAsync(Ct));
        if (response.StatusCode == HttpStatusCode.OK)
        {
            seed.Count($"SELECT count(*) FROM story_sources WHERE id = '{task.SourceId}' AND (http_status IS NULL OR http_status BETWEEN 100 AND 599)")
                .ShouldBe(1, "only a real HTTP status is stored");
        }
    }

    // ------------------------------------------------------------------ worker id and body shape

    [Theory]
    [InlineData("{\"status\":\"done\"}")]
    [InlineData("{\"workerId\":\"\",\"status\":\"failed\"}")]
    [InlineData("{\"workerId\":\"fetcher\\u0000-1\",\"status\":\"failed\"}")]
    [InlineData("{\"workerId\":\"fetcher\\ud800-1\",\"status\":\"failed\"}")]
    [InlineData("{\"workerId\":\"bad worker\",\"status\":\"failed\"}")]
    [InlineData("{\"workerId\":5,\"status\":\"failed\"}")]
    public async Task Result_BadWorkerId_Returns400(string json)
    {
        await AssertRawRefusedAsync(json, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Result_WorkerIdOverSixtyFourCharacters_Returns400()
    {
        await AssertRawRefusedAsync(
            "{\"workerId\":\"" + new string('w', 65) + "\",\"status\":\"failed\"}",
            HttpStatusCode.BadRequest,
            "validation_failed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{\"status\":\"done\"}]")]
    [InlineData("\"done\"")]
    [InlineData("12")]
    [InlineData("{")]
    [InlineData("{\"workerId\":\"fetcher-1\",\"status\":\"done\",}")]
    [InlineData("{\"workerId\":\"fetcher-1\",/*c*/\"status\":\"done\"}")]
    public async Task Result_BodyThatIsNotTheDocumentedObject_Returns400(string json)
    {
        await AssertRawRefusedAsync(json, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Result_DeeplyNestedBody_Returns400_NeverA500()
    {
        var json = "{\"workerId\":\"fetcher-1\",\"status\":\"failed\",\"x\":" + new string('[', 5_000) + new string(']', 5_000) + "}";

        await AssertRawRefusedAsync(json, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Result_CandidatesOnAFetchSourceTask_AreIgnored()
    {
        var (seed, _, task) = await SeedLeasedAsync();
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();
        var body = FetchTaskSeed.DoneBody();
        body["candidates"] = new object[] { new { url = "https://vnexpress.net/a" } };

        using var response = await FetchTaskSeed.PostAsync(client, FetchTaskSeed.Route(task.TaskId, "result"), body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        seed.SourceStatus(task.SourceId).ShouldBe("fetched");
    }

    [Fact]
    public async Task Result_RouteWithABadTaskId_Returns404()
    {
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        foreach (var segment in new[] { "not-a-guid", Guid.Empty.ToString(), Guid.NewGuid().ToString("N"), "../lease" })
        {
            using var response = await FetchTaskSeed.PostAsync(client, $"{FetchTaskSeed.Prefix}/{segment}/result", FetchTaskSeed.DoneBody());
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, segment);
        }
    }

    [Fact]
    public async Task Result_GetOnTheRoute_Returns405()
    {
        using var factory = NewFactory(new InMemoryBlobStore());
        using var client = factory.InternalClient();

        using var response = await client.GetAsync(FetchTaskSeed.Route(Guid.NewGuid(), "result"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }
}
