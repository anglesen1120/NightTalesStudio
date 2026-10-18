using System.Text;
using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Publishing;

/// <summary>
/// QA pass over 06.2.1-T1's domain rules (06 README D6/D7, G4/F08/F09, YR20): the boundaries the task file's
/// §Tests table does not reach — astral-character counting, UTF-8 bytes with multibyte text after the attribution,
/// the real maximum of the tag counter, blank/duplicate/oversized tags, every non-member privacy spelling, the
/// inclusive publish lead, and the aggregate's copy-on-create invariant.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock (every instant is passed in). These are additive QA tests in their own
/// file; the task's own rules stay pinned by <see cref="YouTubeMetadataRulesTests"/> and
/// <see cref="PublicationTests"/>.
/// </remarks>
public sealed class PublicationQaTests
{
    /// <summary>The command's clock reading; every instant in these tests is relative to it.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The default <c>YouTubeOptions.PublishLeadMinutes</c> (06 README D11).</summary>
    private const int LeadMinutes = 120;

    private static readonly Guid UserId = Guid.Parse("0199a2b3-0000-7000-8000-000000000001");
    private static readonly Guid EpisodeId = Guid.Parse("0199a2b3-0000-7000-8000-000000000002");
    private static readonly Guid ChannelConnectionId = Guid.Parse("0199a2b3-0000-7000-8000-000000000003");

    private const string Title = "Night Tales: The Lighthouse";
    private const string Description = "A keeper's last night on the rock.";
    private const string AttributionTail = "\n\nMusic: ";
    private const string Moon = "\U0001F319";

    [Fact]
    public void Title_100Runes_IncludingAstralCharacters_IsAccepted_101IsRefused()
    {
        // A surrogate pair is one character for YouTube: 98 ASCII + 2 astral characters is 100 runes, even though
        // the string is 102 UTF-16 units long. Counting string.Length would refuse a legal title.
        var atCap = new string('a', 98) + Moon + Moon;

        atCap.EnumerateRunes().Count().ShouldBe(100);
        atCap.Length.ShouldBe(102);
        Validate(title: atCap).IsError.ShouldBeFalse();

        var overCap = new string('a', 98) + Moon + Moon + Moon;

        overCap.EnumerateRunes().Count().ShouldBe(101);
        SingleField(Validate(title: overCap)).ShouldBe("title");
    }

    [Fact]
    public void Description_Exactly5000Bytes_WithMultibyteCharsAfterTheAttribution_IsAccepted_5001IsRefused()
    {
        // F08 counts UTF-8 bytes of what YouTube receives: 2,488 x 'é' (2 bytes) = 4,976, the tail 9 bytes, and
        // an attribution of 5 x 'ệ' (3 bytes) = 15 bytes — exactly 5,000.
        var description = Repeat("é", 2_488);
        var attribution = Repeat("ệ", 5);
        var appended = YouTubeMetadataRules.AppendAttribution(description, attribution);

        Encoding.UTF8.GetByteCount(appended).ShouldBe(YouTubeMetadataRules.DescriptionMaxBytes);
        appended.EnumerateRunes().Count().ShouldBe(2_488 + AttributionTail.Length + 5);
        Validate(description: appended).IsError.ShouldBeFalse();

        // One more multibyte rune in the attribution: 5,003 bytes.
        var overByOneRune = YouTubeMetadataRules.AppendAttribution(description, Repeat("ệ", 6));

        Encoding.UTF8.GetByteCount(overByOneRune).ShouldBe(5_003);
        SingleField(Validate(description: overByOneRune)).ShouldBe("description");

        // One more 2-byte rune in the description: 5,002 bytes. The refusal is on the description, not the tail.
        var overByOneByte = YouTubeMetadataRules.AppendAttribution(description + "é", attribution);

        Encoding.UTF8.GetByteCount(overByOneByte).ShouldBe(5_002);
        SingleField(Validate(description: overByOneByte)).ShouldBe("description");
    }

    [Fact]
    public void Tags_TheFiveHundredLengthCap_IsUnreachableThroughALegalList_TheRealMaximumIs494()
    {
        // 15 tags of 30 characters, each carrying one space: 15 x (30 + 2 implicit quotes) + 14 commas = 494.
        var maximum = Enumerable.Range(0, 15).Select(_ => "a" + new string('b', 27) + " c").ToList();

        maximum.ShouldAllBe(tag => tag.EnumerateRunes().Count() == YouTubeMetadataRules.TagMaxChars && tag.Contains(' '));
        YouTubeMetadataRules.TagsLength(maximum).ShouldBe(494);
        Validate(tags: maximum).IsError.ShouldBeFalse();

        // Only a 16th tag can push the counter past 500 (524), and the count rule has already refused that list:
        // the "TagsLength <= 500" rule can therefore never fire on its own (see the QA report's finding F-1).
        var sixteen = maximum.Append("a" + new string('b', 28)).ToList();

        YouTubeMetadataRules.TagsLength(sixteen).ShouldBeGreaterThan(YouTubeMetadataRules.TagsMaxLength);

        var refused = Validate(tags: sixteen);

        refused.IsError.ShouldBeTrue();
        refused.Errors.ShouldAllBe(error => FieldOf(error) == "tags");
    }

    [Fact]
    public void Tags_BlankAndOversized_AreRefused_AndDuplicateTagsAreAcceptedVerbatim()
    {
        SingleField(Validate(tags: [""])).ShouldBe("tags");
        SingleField(Validate(tags: ["   "])).ShouldBe("tags");

        var thirty = new string('t', 30);

        Validate(tags: [thirty]).IsError.ShouldBeFalse();
        SingleField(Validate(tags: [new string('t', 31)])).ShouldBe("tags");

        // No dedup rule exists: 15 identical tags are a legal list (464 by the G4 counter), and storing them
        // verbatim is what the aggregate does.
        var duplicates = Enumerable.Repeat(thirty, 15).ToList();

        YouTubeMetadataRules.TagsLength(duplicates).ShouldBe(464);
        Validate(tags: duplicates).IsError.ShouldBeFalse();
        Create(new PublicationMetadata(Title, Description, duplicates, "private", null)).Tags.Count.ShouldBe(15);
    }

    [Fact]
    public void Tags_AnAstralTag_CountsAsOneRune_So30EmojiAreAcceptedAnd31Refused()
    {
        var thirty = Repeat(Moon, 30);

        thirty.EnumerateRunes().Count().ShouldBe(30);
        thirty.Length.ShouldBe(60);
        Validate(tags: [thirty]).IsError.ShouldBeFalse();
        SingleField(Validate(tags: [Repeat(Moon, 31)])).ShouldBe("tags");
    }

    [Theory]
    [InlineData("PUBLIC")]
    [InlineData("Public")]
    [InlineData("publik")]
    [InlineData("")]
    [InlineData(" public")]
    [InlineData("public ")]
    [InlineData("unlisted ")]
    [InlineData("private\n")]
    public void Privacy_AnySpellingOtherThanTheExactWireValue_IsRefusedOnThePrivacyField(string privacy) =>
        SingleField(Validate(privacy: privacy)).ShouldBe("privacy");

    [Fact]
    public void PublishAt_ExactlyTheLead_IsAccepted_OneSecondLessIsRefused()
    {
        var boundary = Now.AddMinutes(LeadMinutes);

        Validate(publishAt: boundary).IsError.ShouldBeFalse();
        Validate(publishAt: boundary.AddDays(2)).IsError.ShouldBeFalse();
        Validate(publishAt: null).IsError.ShouldBeFalse();
        SingleField(Validate(publishAt: boundary.AddTicks(-TimeSpan.TicksPerSecond))).ShouldBe("publishAt");
    }

    [Fact]
    public void AppendAttribution_EmptyOrWhitespaceAttribution_NeverLeavesABareMusicTail()
    {
        foreach (string? attribution in new string?[] { null, "", " ", "\t\n", "   \r\n " })
        {
            YouTubeMetadataRules.AppendAttribution(Description, attribution).ShouldBe(
                Description,
                $"an attribution of '{attribution}' is absent (06 failure table)");
        }
    }

    [Fact]
    public void Create_CopiesTheTagList_SoTheCallerCannotMutateTheStoredRow()
    {
        var tags = new List<string> { "horror", "story" };
        var publication = Create(new PublicationMetadata(Title, Description, tags, "private", null));

        tags.Add("added-after-create");
        tags[0] = "overwritten";

        publication.Tags.ShouldBe(["horror", "story"]);
    }

    [Fact]
    public void Create_StoresTheAstralTitleAndTheByteExactDescription_AndPinsBothDeclarations()
    {
        var title = new string('a', 98) + Moon + Moon;
        var description = YouTubeMetadataRules.AppendAttribution(Repeat("é", 2_488), Repeat("ệ", 5));

        var publication = Create(new PublicationMetadata(title, description, ["đêm"], "private", null));

        publication.Title.ShouldBe(title, "the domain never rewrites what the handler validated");
        publication.Description.ShouldBe(description);
        Encoding.UTF8.GetByteCount(publication.Description).ShouldBe(5_000);
        publication.Status.ShouldBe(PublicationStatus.AwaitingConsent);
        publication.ContainsSyntheticMedia.ShouldBeTrue();
        publication.SelfDeclaredMadeForKids.ShouldBeFalse();
        publication.ConsentAt.ShouldBeNull();
        publication.ConsentTextVersion.ShouldBeNull();
        publication.VetoEndsAt.ShouldBeNull("a manual publication has no veto window");
    }

    [Fact]
    public void ConsentFromAuthorization_FromAScheduledRow_IsRefusedWithPublicationInvalidState()
    {
        var publication = Create();

        publication.Consent("pc.v1", "pc.v1", Now.AddMinutes(1)).IsError.ShouldBeFalse();

        var second = publication.ConsentFromAuthorization("pc.v1", Guid.CreateVersion7(), Now.AddMinutes(2));

        second.IsError.ShouldBeTrue();
        second.FirstError.Code.ShouldBe(ErrorCodes.PublicationInvalidState);
        publication.ConsentSource.ShouldBe(ConsentSource.User.ToWire(), "the refused write leaves the row alone");
    }

    private static ErrorOr<Success> Validate(
        string? title = null,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        string? privacy = null,
        DateTimeOffset? publishAt = null) =>
        YouTubeMetadataRules.Validate(
            title ?? Title,
            description ?? Description,
            tags ?? [],
            privacy ?? "private",
            publishAt,
            Now,
            LeadMinutes);

    /// <summary>The field of the one violation, or a failure naming what the answer actually held.</summary>
    private static string SingleField(ErrorOr<Success> result)
    {
        result.IsError.ShouldBeTrue("the submission was expected to be refused");

        return FieldOf(result.Errors.ShouldHaveSingleItem());
    }

    private static string FieldOf(Error error) =>
        (string)error.Metadata![YouTubeMetadataRules.FieldMetadataKey];

    private static Publication Create(PublicationMetadata? metadata = null)
    {
        var result = Publication.Create(
            UserId,
            EpisodeId,
            ChannelConnectionId,
            metadata ?? new PublicationMetadata(Title, Description, ["horror", "story"], "private", null),
            autopilotAuthorizationId: null,
            vetoEndsAt: null,
            platformPayloadJson: null,
            Now);

        result.IsError.ShouldBeFalse();

        return result.Value;
    }

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));
}
