using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Features.Publishing.Upload;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// The upload contracts' documented semantics (06.2.3-T1, G7/D6/D17), asserted without any transport: the probe's
/// <see cref="UploadProbe.Partial"/> carries the <em>first byte the session still needs</em> — the <c>Range</c> end
/// plus one, so it is exclusive of the chunk that already arrived — <see cref="VideoInsertRequest.ContainsSyntheticMedia"/>
/// is a value the record carries and never a gate it applies, and <see cref="YouTubeFailure"/> is the one shape the
/// client writes into <see cref="Error.Metadata"/> and 06.2.3-T3's classifier reads back, with a reason sanitised to
/// a short identifier before it ever reaches a log.
/// </summary>
public sealed class YouTubeContractsTests
{
    /// <summary>The <c>Range: bytes=0-N</c> end of one full 256 KiB chunk, as Google's <c>308</c> answers it.</summary>
    private const long LastByteOfOneChunk = (256 * 1024) - 1;

    [Fact]
    public void UploadProbe_PartialNextByte_IsExclusiveOfTheChunkThatArrived()
    {
        // `GoogleYouTubeClient` parses `Range: bytes=0-N` into `Partial(N + 1)` and 06.2.3-T2 resumes from
        // `NextByte`, so N is *inclusive* on the wire and the value both sides use is the byte after it. A layer
        // that resumed at N would re-send one byte of every chunk, which the session refuses.
        var probe = new UploadProbe.Partial(LastByteOfOneChunk + 1);

        probe.NextByte.ShouldBe(256 * 1024);
        probe.NextByte.ShouldBe(LastByteOfOneChunk + 1);
        probe.NextByte.ShouldBeGreaterThan(LastByteOfOneChunk);

        // A session that received nothing answers `Partial(0)`: resume from the first byte, never from 1, and the
        // zero case is exactly what a `308` without a `Range` header becomes.
        new UploadProbe.Partial(0).NextByte.ShouldBe(0);

        // The probe stays a closed set: a completed session carries the video id, an expired one carries nothing.
        var complete = new UploadProbe.Complete("video-1");
        complete.VideoId.ShouldBe("video-1");
        new UploadProbe.Expired().ShouldBeOfType<UploadProbe.Expired>();
    }

    [Fact]
    public void VideoInsertRequest_RequiresSyntheticMediaTrue_AssertedByTheJobNotTheRecord()
    {
        // D6: every upload of this product discloses synthetic media, but the record is the wire shape of
        // `videos.insert` — it carries the flag it is handed and the client sends it verbatim, throwing nothing.
        // The upload job is the one that asserts `true` before a session exists, so the rule lives with the product
        // decision and not in the transport layer.
        var request = new VideoInsertRequest(
            "Title",
            "Description",
            ["tag"],
            "private",
            PublishAt: null,
            ContainsSyntheticMedia: false,
            SelfDeclaredMadeForKids: false,
            Length: 1024,
            MimeType: "video/mp4");

        request.ContainsSyntheticMedia.ShouldBeFalse();
        request.PublishAt.ShouldBeNull();
        request.Length.ShouldBe(1024);
        request.MimeType.ShouldBe("video/mp4");

        // The same record carries `true` verbatim when the caller passes it: the member is data, not a default.
        (request with { ContainsSyntheticMedia = true }).ContainsSyntheticMedia.ShouldBeTrue();
    }

    [Fact]
    public void YouTubeFailure_RoundTripsThroughTheErrorMetadata()
    {
        // The client writes the failure into `Error.Metadata` and 06.2.3-T3's classifier reads it back, so the two
        // sides share this one shape (records compare by value, which is what the round-trip asserts).
        var failure = new YouTubeFailure("quotaExceeded", 403, IsTransport: false);

        var roundTripped = YouTubeFailure.From(
            Error.Failure(YouTubeErrorCodes.QuotaExceeded, "Google's quota is spent.", failure.ToMetadata()));

        roundTripped.ShouldBe(failure);
    }

    [Fact]
    public void YouTubeFailure_MetadataCarriesOnlyTheMembersThatAreSet()
    {
        // A transport failure has no reason and no status: the dictionary must not carry keys with null values, and
        // the transport flag is what makes an error readable back as a failure at all.
        var metadata = new YouTubeFailure(null, null, IsTransport: true).ToMetadata();

        metadata.Keys.ShouldBe([YouTubeFailure.TransportKey]);
        metadata[YouTubeFailure.TransportKey].ShouldBe(true);

        // An error with no YouTube metadata (or with only unrelated keys) is not a YouTube failure: `From` answers
        // null rather than a default failure the classifier would then act on.
        YouTubeFailure.From(Error.Failure("other", "No metadata.")).ShouldBeNull();
        YouTubeFailure.From(
            Error.Failure("other", "Unrelated metadata.", new Dictionary<string, object> { ["other"] = 1 }))
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("quotaExceeded", "quotaExceeded")]
    [InlineData("invalid_publish_at", "invalid_publish_at")]
    [InlineData("uploadLimitExceeded", "uploadLimitExceeded")]
    [InlineData("a.b-c_d", "a.b-c_d")]
    [InlineData("x", "x")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("two words", null)]
    [InlineData("inject\nnewline", null)]
    [InlineData("05:00", null)]
    [InlineData("rejected\u00df", null)]
    public void SanitizeReason_KeepsShortIdentifiersOnly(string? reason, string? expected) =>
        YouTubeFailure.SanitizeReason(reason).ShouldBe(expected);

    [Fact]
    public void SanitizeReason_DropsAReasonPastTheMaximumLength()
    {
        // An untrusted body must not stretch a log line or a column, so the bound itself is pinned: the longest
        // accepted reason is kept and the next character over is dropped.
        var atLimit = new string('a', YouTubeFailure.MaxReasonLength);
        var oneOverLimit = new string('a', YouTubeFailure.MaxReasonLength + 1);

        YouTubeFailure.MaxReasonLength.ShouldBe(64);
        YouTubeFailure.SanitizeReason(atLimit).ShouldBe(atLimit);
        YouTubeFailure.SanitizeReason(oneOverLimit).ShouldBeNull();
    }
}
