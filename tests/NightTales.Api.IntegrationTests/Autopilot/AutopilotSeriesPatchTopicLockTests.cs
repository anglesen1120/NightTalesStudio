using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NightTales.Api.Http.Filters;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// 02.8.3-T2 §Acceptance 7 at the wire, not only in the aggregate: <c>PATCH /series/{id}</c> that changes the topic of
/// an autopilot-enabled series answers 409 <c>series_topic_locked</c> — and changes nothing — while the same patch on a
/// disabled series (or one that leaves the topic alone) is accepted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The lock was covered only by the domain test of 09.4.1-T1 (<c>SeriesTests</c>), and the
/// topic refusal the settings suite asserts is the <em>PUT</em> one (the covered-field and channel rungs), never this
/// route. The wire code is <c>series_topic_locked</c> — the merged 09 D16/D17 spelling, which 02's D-2 keeps — so the
/// assertion below is the one a client can act on (02.8.3-T2 QA D6).
/// </para>
/// <para>
/// <b>Why ghost → spiritual.</b> The Application handler re-checks the stored style template before the aggregate
/// decides (<c>UpdateSeriesHandler.CheckStyleTemplateAsync</c>), and a template without a topic hint is refused for
/// <c>true_crime</c> — that refusal is 400 <c>validation_failed</c> and would hide the 409 this file is about. Both ghost
/// and spiritual satisfy <c>StyleTopicPolicy</c> for the builder's hint-less template, so the request reaches the
/// aggregate's topic lock.
/// </para>
/// <para>
/// Every case seeds its own user (the tenant filter keeps the class database's rows apart) and enables autopilot by
/// writing the columns the way the other autopilot suites do, because the settings route is not what is under test here.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
public sealed class AutopilotSeriesPatchTopicLockTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The series routes of 09.4.1 (a frozen path, as in <c>SeriesEndpointsTests</c>).</summary>
    private const string SeriesRoute = "/api/v1/series";

    /// <summary>The wire code the aggregate answers (09 README D16/D17, 02 D-2).</summary>
    private const string TopicLockedCode = "series_topic_locked";

    /// <summary>A fixed instant, so every seeded row shares the clock the host reads.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// §Acceptance 7: changing the topic of an enabled series is refused with 409 <c>series_topic_locked</c>, the stored
    /// row is untouched (topic and switch), and the tag does not move — the refusal rolled back with the command.
    /// </summary>
    [Fact]
    public async Task PatchSeries_TopicChangeWhileAutopilotEnabled_Returns409SeriesTopicLocked()
    {
        var clock = new FakeClock(Start);
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId, autopilotEnabled: true);
        using var factory = new ApiFactory(Database, clock);
        using var client = factory.ClientFor(userId);

        var tag = await ReadETagAsync(client, seriesId);

        using var response = await PatchAsync(client, seriesId, new { topic = "spiritual" }, tag);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe(TopicLockedCode);
        problem.Status.ShouldBe((int)HttpStatusCode.Conflict);

        StoredTopic(seriesId).ShouldBe("ghost", "a refused patch writes nothing");
        AutopilotEnabled(seriesId).ShouldBeTrue("the refusal must not switch autopilot off");
        (await ReadETagAsync(client, seriesId)).ShouldBe(tag, "the rolled-back command did not touch the row");
    }

    /// <summary>
    /// The control that makes the refusal about a <em>different</em> topic: sending the topic the series already has is
    /// accepted while autopilot is on (the aggregate only refuses a change).
    /// </summary>
    [Fact]
    public async Task PatchSeries_SameTopicWhileAutopilotEnabled_IsAccepted()
    {
        var clock = new FakeClock(Start);
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId, autopilotEnabled: true);
        using var factory = new ApiFactory(Database, clock);
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(client, seriesId, new { topic = "ghost" }, await ReadETagAsync(client, seriesId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        StoredTopic(seriesId).ShouldBe("ghost");
    }

    /// <summary>
    /// The lock is topic-scoped, not a general freeze: another field of an enabled series is still editable (09 README
    /// D16 lists only the topic).
    /// </summary>
    [Fact]
    public async Task PatchSeries_OtherFieldWhileAutopilotEnabled_IsAccepted()
    {
        var clock = new FakeClock(Start);
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId, autopilotEnabled: true);
        using var factory = new ApiFactory(Database, clock);
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(
            client,
            seriesId,
            new { title = "Chuỗi đổi tên" },
            await ReadETagAsync(client, seriesId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        using var read = await client.GetAsync($"{SeriesRoute}/{seriesId}", Ct);
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("title").GetString().ShouldBe("Chuỗi đổi tên");
    }

    /// <summary>
    /// With autopilot off the same topic change is a normal patch: 200 and the column moves. This is the half a client
    /// performs before it re-accepts the authorization text.
    /// </summary>
    [Fact]
    public async Task PatchSeries_TopicChangeWhileAutopilotDisabled_IsAccepted()
    {
        var clock = new FakeClock(Start);
        var userId = await CreateUserAsync();
        var seriesId = await SeedSeriesAsync(userId, autopilotEnabled: false);
        using var factory = new ApiFactory(Database, clock);
        using var client = factory.ClientFor(userId);

        using var response = await PatchAsync(
            client,
            seriesId,
            new { topic = "spiritual" },
            await ReadETagAsync(client, seriesId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        StoredTopic(seriesId).ShouldBe("spiritual");
    }

    private async Task<Guid> CreateUserAsync() =>
        (await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct)).Id;

    /// <summary>
    /// Inserts one series through the TestKit builder with the autopilot columns already written: the builder flips
    /// <c>autopilot_enabled</c> and stores a valid §3.1 settings document, which is the state the lock reads.
    /// </summary>
    /// <param name="userId">The owner.</param>
    /// <param name="autopilotEnabled">The <c>autopilot_enabled</c> column.</param>
    /// <returns>The seeded series.</returns>
    private async Task<Guid> SeedSeriesAsync(Guid userId, bool autopilotEnabled)
    {
        var settings = AutopilotSettingsValidator.Serialize(AutopilotTickHarness.Settings());

        var series = await new SeriesBuilder(Database, new TestCurrentUser { Id = userId }, new FakeClock(Start))
            .CreateAsync(
                userId,
                title: "Chuỗi thử nghiệm",
                topic: Topic.Ghost,
                autopilotEnabled: autopilotEnabled,
                autopilotSettings: settings,
                ct: Ct);

        return series.Id;
    }

    private static async Task<HttpResponseMessage> PatchAsync<TBody>(
        HttpClient client,
        Guid seriesId,
        TBody body,
        string? ifMatch)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{SeriesRoute}/{seriesId.ToString("D", CultureInfo.InvariantCulture)}")
        {
            Content = JsonContent.Create(body),
        };

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation(RequireIfMatchFilter.HeaderName, ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string> ReadETagAsync(HttpClient client, Guid seriesId)
    {
        using var response = await client.GetAsync(
            $"{SeriesRoute}/{seriesId.ToString("D", CultureInfo.InvariantCulture)}",
            Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        response.Headers.ETag.ShouldNotBeNull("the read publishes the tag the patch demands");

        return response.Headers.ETag!.ToString();
    }

    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe((int)response.StatusCode);

        return problem;
    }

    /// <summary>The stored <c>series.topic</c>, as the filter the client sees would read it.</summary>
    /// <param name="seriesId">The series.</param>
    /// <returns>The wire spelling of the column.</returns>
    private string StoredTopic(Guid seriesId) => SqlProbe.ScalarText(
        Database,
        $"SELECT topic FROM series WHERE id = '{seriesId.ToString("D", CultureInfo.InvariantCulture)}'");

    /// <summary>Whether <c>series.autopilot_enabled</c> is set.</summary>
    /// <param name="seriesId">The series.</param>
    /// <returns>The column's value.</returns>
    private bool AutopilotEnabled(Guid seriesId) => SqlProbe.ScalarText(
        Database,
        $"SELECT autopilot_enabled::text FROM series WHERE id = '{seriesId.ToString("D", CultureInfo.InvariantCulture)}'")
        == "true";

    /// <summary>The ProblemDetails members this class asserts on (09 README D7).</summary>
    /// <param name="Code">The stable code.</param>
    /// <param name="Status">The HTTP status the body repeats.</param>
    /// <param name="Errors">The field groups, or null when the body carries none.</param>
    private sealed record ProblemBody(string Code, int Status, Dictionary<string, string[]>? Errors);
}
