using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.TestKit.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>The invariants of <see cref="StorySource"/> (02.5.1 plan §3.1).</summary>
public sealed class StorySourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Sha = new('a', 64);

    private static StorySource PendingUrl() => StorySource.CreateUrl(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        SourceUrl.Parse("https://example.com/a", new FakePublicSuffixList()).Value,
        Now).Value;

    [Fact]
    public void CreateUrl_SetsUrlFieldsAndPending()
    {
        var source = PendingUrl();

        source.Kind.ShouldBe(StorySourceKind.Url);
        source.Status.ShouldBe(StorySourceStatus.Pending);
        source.NormalizedUrl.ShouldBe("https://example.com/a");
        source.RegistrableDomain.ShouldBe("example.com");
        source.NoteText.ShouldBeNull();
    }

    [Fact]
    public void CreateNote_IsFetchedWithHashOfNfcText()
    {
        var source = StorySource.CreateNote(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "  Tie\u0301ng Viet  ", Now).Value;

        source.Kind.ShouldBe(StorySourceKind.UserNote);
        source.Status.ShouldBe(StorySourceStatus.Fetched);
        source.NoteText.ShouldBe("Tie\u0301ng Viet", "the stored note is trimmed");
        source.Url.ShouldBeNull();
        source.TextChars.ShouldBe(source.NoteText!.Length);
        source.ContentSha256.ShouldNotBeNull().Length.ShouldBe(64);
        source.ExtractedTextKey.ShouldBeNull();
        source.FetchedAt.ShouldBe(Now);

        // NFC: the decomposed and the composed spelling of one note hash alike.
        var composed = StorySource.CreateNote(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Ti\u00e9ng Viet", Now).Value;
        source.ContentSha256.ShouldBe(composed.ContentSha256);
    }

    [Fact]
    public void CreateUrl_WithoutUrl_Fails()
    {
        var empty = Should.Throw<ArgumentNullException>(() => StorySource.CreateUrl(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null!, Now));
        empty.ParamName.ShouldBe("url");
    }

    [Fact]
    public void CreateNote_WithUrl_Fails()
    {
        // The note factory has no URL parameter at all: a note can never carry one. A URL pasted into a note is text.
        var source = StorySource.CreateNote(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "https://example.com", Now).Value;

        source.Url.ShouldBeNull();
        source.NoteText.ShouldBe("https://example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateNote_Empty_Fails(string? text)
        => StorySource.CreateNote(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), text, Now).IsError.ShouldBeTrue();

    [Fact]
    public void CreateNote_Over3000_Fails()
    {
        StorySource.CreateNote(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('x', 3_001), Now).IsError.ShouldBeTrue();
        StorySource.CreateNote(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('x', 3_000), Now).IsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("A0000000000000000000000000000000000000000000000000000000000000AA")]
    public void MarkFetched_WithoutSha256_Fails(string sha)
        => Should.Throw<ArgumentException>(() => PendingUrl().MarkFetched("https://example.com/a", null, "text/html", sha, "k", 1, Now));

    [Fact]
    public void MarkFetched_StoresResult()
    {
        var source = PendingUrl();

        source.MarkFetched("https://example.com/final", "Title", "text/html", Sha, "research/k.txt", 120, Now);

        source.Status.ShouldBe(StorySourceStatus.Fetched);
        source.ContentSha256.ShouldBe(Sha);
        source.TextChars.ShouldBe(120);
        source.FetchedAt.ShouldBe(Now);
    }

    [Fact]
    public void Retry_FromFailed_BecomesPending()
    {
        var source = PendingUrl();
        source.MarkFailed("source_unreachable", 503, Now);

        source.Retry(Now).IsError.ShouldBeFalse();

        source.Status.ShouldBe(StorySourceStatus.Pending);
        source.ErrorCode.ShouldBeNull();
        source.HttpStatus.ShouldBeNull();
    }

    [Fact]
    public void Retry_FromFetched_Fails()
    {
        var source = PendingUrl();
        source.MarkFetched("https://example.com/a", null, "text/html", Sha, "k", 1, Now);

        var result = source.Retry(Now);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(Episode.InvalidStateCode);
    }
}
