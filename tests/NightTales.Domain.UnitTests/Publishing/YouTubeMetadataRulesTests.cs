using System.Text;
using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Publishing;

/// <summary>
/// The YouTube metadata contract of 06 README D6 (G4/G6): the byte/character split of F08, the tag counter of G4
/// (F09), the publish lead time of D6, and the rule that a 400 names every broken field rather than the first one.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock (every instant is passed in). The 201/400 round trips over a real HTTP
/// body belong to 06.2.1-T1's Api integration tests, and the pre-audit privacy override of D12 to its Application
/// tests.
/// </remarks>
public sealed class YouTubeMetadataRulesTests
{
    /// <summary>The command's clock reading; every publish instant in these tests is relative to it.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The default <c>YouTubeOptions.PublishLeadMinutes</c> (06 README D11).</summary>
    private const int LeadMinutes = 120;

    private const string Title = "Night Tales: The Lighthouse";
    private const string Description = "A keeper's last night on the rock.";

    /// <summary>The tail <see cref="YouTubeMetadataRules.AppendAttribution"/> appends (C8, ruling T9).</summary>
    private const string AttributionTail = "\n\nMusic: ";

    [Fact]
    public void Description_Bytes_AfterAttribution()
    {
        // F08: the cap counts the bytes of the description YouTube receives, so it is the appended text that is
        // measured. 4,981 + "\n\nMusic: " (9) + a 10-character attribution is exactly 5,000 bytes: accepted.
        var description = new string('a', 4_981);
        var atCap = YouTubeMetadataRules.AppendAttribution(description, new string('b', 10));

        Encoding.UTF8.GetByteCount(atCap).ShouldBe(YouTubeMetadataRules.DescriptionMaxBytes);
        Validate(description: atCap).IsError.ShouldBeFalse();

        // One byte more is refused, and the field named is the description, not the attribution.
        var overCap = YouTubeMetadataRules.AppendAttribution(description, new string('b', 11));

        Encoding.UTF8.GetByteCount(overCap).ShouldBe(YouTubeMetadataRules.DescriptionMaxBytes + 1);
        SingleField(Validate(description: overCap)).ShouldBe("description");

        // The case the task calls out: a description that is legal on its own is pushed over the cap by the
        // attribution that is appended after it was written (4,999 bytes + the tail + a name).
        var pushed = YouTubeMetadataRules.AppendAttribution(new string('a', 4_999), "Night Tales");

        Encoding.UTF8.GetByteCount(pushed).ShouldBeGreaterThan(YouTubeMetadataRules.DescriptionMaxBytes);
        SingleField(Validate(description: pushed)).ShouldBe("description");

        // The attribution is appended, never validated instead of the description: an empty tail leaves 4,999
        // bytes, which is accepted.
        Validate(description: YouTubeMetadataRules.AppendAttribution(new string('a', 4_999), string.Empty))
            .IsError.ShouldBeFalse();
    }

    [Fact]
    public void Description_Bytes_CountUtf8NotCharacters()
    {
        // F08: a character count would accept all of these. 1,667 three-byte characters are 5,001 bytes.
        var threeByte = new string('\u1ebf', 1_667);

        Encoding.UTF8.GetByteCount(threeByte).ShouldBe(YouTubeMetadataRules.DescriptionMaxBytes + 1);
        Validate(description: threeByte).IsError.ShouldBeTrue();
        Validate(description: new string('\u1ebf', 1_666)).IsError.ShouldBeFalse();

        // An astral character is one character and four bytes: 1,250 of them are exactly the cap, 1,251 are not.
        Validate(description: Repeat("\ud83d\ude00", 1_250)).IsError.ShouldBeFalse();
        SingleField(Validate(description: Repeat("\ud83d\ude00", 1_251))).ShouldBe("description");
    }

    [Fact]
    public void Description_AllowsLineBreaks_RejectsOtherControlCharacters()
    {
        // A description is free-form text, so \n, \r and \t are legal; every other C0 control and DEL is not.
        Validate(description: "Line one\nLine two\r\nColumn\tvalue").IsError.ShouldBeFalse();

        foreach (var control in new[] { "a\u0000b", "a\u001fb", "a\u0007b", "a\u007fb" })
        {
            SingleField(Validate(description: control)).ShouldBe("description", $"{control} carries a control character");
        }
    }

    [Fact]
    public void Title_CountsCharacters_NotSurrogatePairs()
    {
        // G4 in characters: 100 is accepted even when it is 100 astral characters (400 bytes, 200 UTF-16 units,
        // which a length check would have refused long before the cap).
        Validate(title: Repeat("\ud83d\ude00", 100)).IsError.ShouldBeFalse();
        SingleField(Validate(title: Repeat("\ud83d\ude00", 101))).ShouldBe("title");

        Validate(title: new string('a', YouTubeMetadataRules.TitleMaxChars)).IsError.ShouldBeFalse();
        SingleField(Validate(title: new string('a', YouTubeMetadataRules.TitleMaxChars + 1))).ShouldBe("title");

        // A title is never empty: the lower bound is part of the same rule.
        SingleField(Validate(title: string.Empty)).ShouldBe("title");
    }

    [Fact]
    public void Title_And_Description_RejectAngleBrackets()
    {
        // D6's "< >" rule, on the two fields YouTube allows markup-like text in (a tag has its own rule).
        SingleField(Validate(title: "Night <Tales>")).ShouldBe("title");
        SingleField(Validate(description: "A <b>keeper</b>")).ShouldBe("description");
    }

    [Fact]
    public void Title_RejectsControlCharacters()
    {
        // Unlike the description, a title carries no line break either.
        foreach (var control in new[] { "Night\nTales", "Night\rTales", "Night\tTales", "Night\u0000Tales", "Night\u007fTales" })
        {
            SingleField(Validate(title: control)).ShouldBe("title", $"{control} carries a control character");
        }
    }

    [Fact]
    public void Tags_Length_CountsCommasAndImplicitQuotes()
    {
        // G4/F09, hand-computed: "night tales" is 11 characters plus the 2 implicit quotes YouTube adds around a
        // tag with a space, "story" is 5, "podcast" is 7, and 2 commas separate the three tags.
        string[] tags = ["night tales", "story", "podcast"];

        YouTubeMetadataRules.TagsLength(tags).ShouldBe((11 + 2) + 5 + 7 + 2);
        YouTubeMetadataRules.TagsLength(tags).ShouldBe(27);

        // A tag without a space costs nothing extra, and an empty list is 0 rather than the formula's -1.
        YouTubeMetadataRules.TagsLength(["abcdefghij", "k"]).ShouldBe(10 + 1 + 1);
        YouTubeMetadataRules.TagsLength([]).ShouldBe(0);
    }

    [Fact]
    public void Tags_Length_AcceptsTheCap_RejectsOneMore()
    {
        // The list cap of G4 is a second, platform-level guard: with at most 15 tags of at most 30 characters the
        // first two rules already cap the G4 length at 15 x (30 + 2) + 14 = 494, so only a direct call can reach
        // 500 — and 500 is accepted while 501 is not.
        YouTubeMetadataRules.TagsLength([new string('a', 250), new string('a', 249)]).ShouldBe(500);
        YouTubeMetadataRules.TagsLength([new string('a', 250), new string('a', 250)]).ShouldBe(501);

        // Reaching the list cap necessarily breaks the per-tag cap too, so the answer names the same field twice.
        Validate(tags: [new string('a', 250), new string('a', 250)]).Errors
            .Select(FieldOf)
            .Distinct(StringComparer.Ordinal)
            .ShouldBe(["tags"]);
    }

    [Fact]
    public void Tags_Accept15_Reject16()
    {
        var fifteen = Enumerable.Range(0, YouTubeMetadataRules.TagsMaxCount).Select(index => $"tag{index}").ToList();
        var sixteen = fifteen.Append("tag15").ToList();

        Validate(tags: fifteen).IsError.ShouldBeFalse();
        SingleField(Validate(tags: sixteen)).ShouldBe("tags");
    }

    [Fact]
    public void Tags_EachTag_IsBoundedAndCarriesNoMarkup()
    {
        // 30 characters, counted the way the title is: 30 astral characters are 30, not 60.
        Validate(tags: [Repeat("\ud83d\ude00", YouTubeMetadataRules.TagMaxChars)]).IsError.ShouldBeFalse();
        SingleField(Validate(tags: [Repeat("\ud83d\ude00", YouTubeMetadataRules.TagMaxChars + 1)])).ShouldBe("tags");
        SingleField(Validate(tags: [new string('a', YouTubeMetadataRules.TagMaxChars + 1)])).ShouldBe("tags");

        SingleField(Validate(tags: ["a<b"])).ShouldBe("tags");
        SingleField(Validate(tags: ["a>b"])).ShouldBe("tags");
        SingleField(Validate(tags: ["a\u0007b"])).ShouldBe("tags");

        // A blank tag is a client bug rather than a shorter list.
        SingleField(Validate(tags: [string.Empty])).ShouldBe("tags");
        SingleField(Validate(tags: ["   "])).ShouldBe("tags");
    }

    [Theory]
    [InlineData("public")]
    [InlineData("unlisted")]
    [InlineData("private")]
    public void Privacy_AcceptsTheThreeWireValues(string privacy) => Validate(privacy: privacy).IsError.ShouldBeFalse();

    [Theory]
    [InlineData("Public")]
    [InlineData("hidden")]
    [InlineData("")]
    [InlineData("unlisted ")]
    public void Privacy_RejectsAnythingElse(string privacy) => SingleField(Validate(privacy: privacy)).ShouldBe("privacy");

    [Fact]
    public void PublishAt_RespectsPublishLeadMinutes()
    {
        // D6: 120 minutes ahead is the inclusive boundary (the task's failure table), 119 is refused and 121 is a
        // later slot the owner is free to ask for. No publish time at all is "as soon as it is processed".
        Validate(publishAt: Now.AddMinutes(LeadMinutes)).IsError.ShouldBeFalse();
        Validate(publishAt: Now.AddMinutes(LeadMinutes + 1)).IsError.ShouldBeFalse();
        Validate(publishAt: null).IsError.ShouldBeFalse();

        SingleField(Validate(publishAt: Now.AddMinutes(LeadMinutes - 1))).ShouldBe("publishAt");
        SingleField(Validate(publishAt: Now)).ShouldBe("publishAt");
        SingleField(Validate(publishAt: Now.AddMinutes(-1))).ShouldBe("publishAt");

        // The lead time is a parameter, not a constant here, so the boundary follows the option (D11).
        Validate(publishAt: Now.AddMinutes(1), publishLeadMinutes: 1).IsError.ShouldBeFalse();
        SingleField(Validate(publishAt: Now.AddSeconds(59), publishLeadMinutes: 1)).ShouldBe("publishAt");
    }

    [Fact]
    public void Validate_CollectsEveryViolation_InOneAnswer()
    {
        // The whole point of the writer contract: a client learns all five broken fields from one 400 instead of
        // fixing them one round trip at a time.
        var result = Validate(
            title: new string('a', YouTubeMetadataRules.TitleMaxChars + 1),
            description: "A <b>keeper</b>",
            tags: [.. Enumerable.Range(0, YouTubeMetadataRules.TagsMaxCount + 1).Select(index => $"tag{index}"), string.Empty],
            privacy: "hidden",
            publishAt: Now.AddMinutes(1));

        result.IsError.ShouldBeTrue();
        result.Errors.ShouldAllBe(error => error.Type == ErrorType.Validation);
        result.Errors.ShouldAllBe(error => error.Code == YouTubeMetadataRules.ValidationFailedCode);

        result.Errors
            .Select(FieldOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["description", "privacy", "publishAt", "tags", "title"]);

        // Each field is named once per broken rule, never as an empty group.
        result.Errors.ShouldAllBe(error => error.Metadata!.ContainsKey(YouTubeMetadataRules.FieldMetadataKey));
    }

    [Fact]
    public void AppendAttribution_Null_Or_Blank_LeavesDescriptionUntouched()
    {
        // The failure table of the task: an attribute-less render (05 README:332-336), an empty attribution and a
        // blank one all leave the description as sent — no "Music: " tail with no value.
        foreach (string? attribution in new string?[] { null, string.Empty, "   " })
        {
            YouTubeMetadataRules.AppendAttribution(Description, attribution)
                .ShouldBe(Description, $"an attribution of '{attribution}' is absent");
        }
    }

    [Fact]
    public void AppendAttribution_AppendsTheMusicTail()
    {
        var appended = YouTubeMetadataRules.AppendAttribution(Description, "Kai Engel - Snowmen");

        appended.ShouldBe(Description + AttributionTail + "Kai Engel - Snowmen");
        appended.ShouldStartWith(Description);
    }

    [Fact]
    public void Codes_MatchTheErrorCatalogue()
    {
        // The Domain cannot reference Application/Common/Errors (ErrorOr is its only package), so the two keys it
        // writes are spelled here; this test is the join between them (the FactIdAllocator precedent).
        YouTubeMetadataRules.ValidationFailedCode.ShouldBe(ErrorCodes.ValidationFailed);
        YouTubeMetadataRules.FieldMetadataKey.ShouldBe(ErrorMetadata.Field);
    }

    private static ErrorOr<Success> Validate(
        string? title = null,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        string? privacy = null,
        DateTimeOffset? publishAt = null,
        DateTimeOffset? now = null,
        int publishLeadMinutes = LeadMinutes) =>
        YouTubeMetadataRules.Validate(
            title ?? Title,
            description ?? Description,
            tags ?? [],
            privacy ?? "private",
            publishAt,
            now ?? Now,
            publishLeadMinutes);

    /// <summary>The field of the one violation, or a failure naming what the answer actually held.</summary>
    private static string SingleField(ErrorOr<Success> result)
    {
        result.IsError.ShouldBeTrue();
        return FieldOf(result.Errors.ShouldHaveSingleItem());
    }

    private static string FieldOf(Error error) => (string)error.Metadata![YouTubeMetadataRules.FieldMetadataKey];

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));
}
