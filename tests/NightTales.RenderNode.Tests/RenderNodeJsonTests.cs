using System.Text.Json;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The two modes of 11 README D4 and the <c>Z</c> timestamp of 05.4.1-T2 §1. The duplicate-property rejection is
/// .NET 10's built-in <c>JsonSerializerOptions.AllowDuplicateProperties</c> (E18, ruling T1); no pre-scan is
/// needed, and these are the tests that keep that true.
/// </summary>
public sealed class RenderNodeJsonTests
{
    private const string StatusWithExtraMember =
        """
        {"schema":"render-status.v1","task_id":"0192f3c1-8a4b-7c2d-9e10-3f5a6b7c8d90",
         "render_job_id":"0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8e9f","state":"queued","stage":"queued","percent":0,
         "cancel_requested":false,"submitted_at":"2026-09-26T15:11:20Z","started_at":null,"finished_at":null,
         "outcome":null,"output":null,"check":null,"images":null,"zzz_added_by_v2":true,
         "mpt":{"state":4,"progress":0,"failed_stage":null,"warnings":[]}}
        """;

    private const string StatusWithDuplicateMember =
        """
        {"schema":"render-status.v1","task_id":"0192f3c1-8a4b-7c2d-9e10-3f5a6b7c8d90",
         "render_job_id":"0192f3c1-7a3b-7d1e-8f20-4a5b6c7d8e9f","state":"queued","stage":"queued","percent":0,
         "cancel_requested":false,"submitted_at":"2026-09-26T15:11:20Z","submitted_at":"2026-09-26T15:11:21Z",
         "started_at":null,"finished_at":null,"outcome":null,"output":null,"check":null,"images":null,
         "mpt":{"state":4,"progress":0,"failed_stage":null,"warnings":[]}}
        """;

    [Fact]
    public void Strict_RejectsUnknownMember()
    {
        var exception = Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<RenderStatusV1>(StatusWithExtraMember, RenderNodeJson.Strict));

        exception.Path.ShouldBe("$.zzz_added_by_v2");
    }

    [Fact]
    public void Strict_RejectsDuplicateProperty()
    {
        var exception = Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<RenderStatusV1>(StatusWithDuplicateMember, RenderNodeJson.Strict));

        exception.Message.ShouldContain("Duplicate");
    }

    [Fact]
    public void Tolerant_IgnoresUnknownMember()
    {
        var status = JsonSerializer.Deserialize<RenderStatusV1>(StatusWithExtraMember, RenderNodeJson.Tolerant);

        status.ShouldNotBeNull();
        status.State.ShouldBe(RenderStates.Queued);
        status.Mpt.ShouldNotBeNull();
    }

    [Fact]
    public void Timestamps_RoundTripWithZ()
    {
        var submittedAt = new DateTimeOffset(2026, 9, 26, 15, 11, 20, TimeSpan.Zero);
        var status = new RenderStatusV1(
            RenderNodeSchemas.RenderStatus,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            RenderStates.Queued,
            RenderStages.Queued,
            0,
            false,
            submittedAt,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var json = JsonSerializer.Serialize(status, RenderNodeJson.Strict);

        json.ShouldContain("\"submitted_at\":\"2026-09-26T15:11:20Z\"");
        json.ShouldContain("\"started_at\":null");
        // An offset instant is the same value as its Z spelling (§1).
        var shifted = JsonSerializer.Deserialize<RenderStatusV1>(
            json.Replace("2026-09-26T15:11:20Z", "2026-09-26T22:11:20+07:00", StringComparison.Ordinal),
            RenderNodeJson.Strict);
        shifted.ShouldNotBeNull();
        shifted.SubmittedAt.ShouldBe(submittedAt);
        shifted.SubmittedAt.Offset.ShouldBe(TimeSpan.Zero);
    }
}
