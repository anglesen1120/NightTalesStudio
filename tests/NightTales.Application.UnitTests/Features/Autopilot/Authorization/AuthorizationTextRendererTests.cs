using System.Security.Cryptography;
using System.Text;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Authorization;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Authorization;

/// <summary>
/// <see cref="AuthorizationTextRenderer"/> (02.8.3-T2, task 'Authorization text' and its Tests section): the sentence
/// the owner authorizes, its two locale templates, the label maps and the <c>sha256(NFC(text))</c> digest the accept
/// command compares.
/// </summary>
public sealed class AuthorizationTextRendererTests
{
    /// <summary>The renderer under test; it holds only the two templates it reads once, so one instance serves every test.</summary>
    private static readonly AuthorizationTextRenderer Renderer = new();

    /// <summary>The weekdays of every fixture, deliberately not in the order the text reads them in.</summary>
    private static readonly DayOfWeek[] BusinessWeek = [DayOfWeek.Thursday, DayOfWeek.Monday];

    /// <summary>The composed (NFC) Vietnamese title the NFD fixture normalises to.</summary>
    private const string ComposedTitle = "Cà phê đêm";

    /// <summary>
    /// Every placeholder is substituted in both locales: a <c>{{</c> or <c>}}</c> left in the sentence would reach
    /// the owner as literal braces, which is exactly what the task's first unit test forbids.
    /// </summary>
    /// <param name="locale">The wire locale.</param>
    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void SubstitutesEveryPlaceholder(string locale)
    {
        var result = Renderer.Render(Request(locale));

        var rendered = result.Value;

        rendered.Locale.ShouldBe(locale);
        rendered.TextVersion.ShouldBe(AuthorizationTextRenderer.CurrentTextVersion);
        rendered.Text.ShouldNotContain("{{");
        rendered.Text.ShouldNotContain("}}");
    }

    /// <summary>
    /// The same inputs render the same sentence and the same digest, and that digest is the task's formula —
    /// <c>sha256(UTF-8(NFC(text)))</c> in lower-case hex — computed here independently of the renderer.
    /// </summary>
    [Fact]
    public void HashIsStableForSameInputs()
    {
        var first = Renderer.Render(Request("vi")).Value;
        var second = Renderer.Render(Request("vi")).Value;

        second.Text.ShouldBe(first.Text);
        second.TextHash.ShouldBe(first.TextHash);
        first.TextHash.ShouldBe(Sha256(first.Text));
        first.TextHash.Length.ShouldBe(64);
        first.TextHash.ShouldBe(first.TextHash.ToLowerInvariant());
    }

    /// <summary>
    /// A title in NFD (the form a paste from macOS can produce) is NFC-normalised before it is shown and hashed, so
    /// the digest the client computes over what it displays matches the server's.
    /// </summary>
    [Fact]
    public void NfdTitle_IsNormalisedBeforeHashing()
    {
        var nfdTitle = ComposedTitle.Normalize(NormalizationForm.FormD);
        nfdTitle.ShouldNotBe(ComposedTitle);

        var rendered = Renderer.Render(Request("vi", seriesTitle: nfdTitle)).Value;

        rendered.Text.ShouldContain(ComposedTitle);
        rendered.Text.ShouldNotContain("e\u0302");
        rendered.Text.ShouldNotContain("a\u0300");
        rendered.TextHash.ShouldBe(Sha256(rendered.Text));
    }

    /// <summary>
    /// The pre-audit note appears only while the channel's audit is pending, and an absent note leaves no double space
    /// behind at the join.
    /// </summary>
    /// <param name="locale">The wire locale.</param>
    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void PreAuditNote_PresentOnlyWhenPending(string locale)
    {
        var pending = Renderer.Render(Request(locale, preAuditPending: true)).Value;
        var approved = Renderer.Render(Request(locale, preAuditPending: false)).Value;

        pending.Text.ShouldContain(PreAuditNote(locale));
        approved.Text.ShouldNotContain(PreAuditNote(locale));
        approved.Text.ShouldNotContain("  ");
        pending.Text.ShouldNotContain("  ");
        approved.TextHash.ShouldNotBe(pending.TextHash);
    }

    /// <summary>
    /// The Vietnamese sentence carries every covered value of the request: the series title, the topic label, the
    /// channel, the weekday names, the publish time, the time zone, the daily cap, the privacy label and the veto
    /// window — the fields the authorization is about.
    /// </summary>
    [Fact]
    public void VietnameseText_ContainsAllCoveredFields()
    {
        var rendered = Renderer.Render(Request("vi")).Value;
        string[] expected =
        [
            "Midnight Files",
            "truyện ma",
            "Night Tales Channel",
            "Thứ Hai, Thứ Năm",
            "19:00",
            "Asia/Ho_Chi_Minh",
            "2",
            "không công khai (chỉ có link)",
            "6",
        ];

        foreach (var value in expected)
        {
            rendered.Text.ShouldContain(value);
        }

        // The reviewed labels of the other two topics and the other two privacies, so a swapped map fails here.
        foreach (var (topic, label) in new[]
                 {
                     (Topic.Spiritual, "truyện tâm linh"),
                     (Topic.TrueCrime, "true crime (người thật, luôn do tôi duyệt)"),
                 })
        {
            Renderer.Render(Request("vi", topic: topic)).Value.Text.ShouldContain(label);
        }

        foreach (var (privacy, label) in new[]
                 {
                     (PrivacyStatus.Public, "công khai"),
                     (PrivacyStatus.Private, "riêng tư"),
                 })
        {
            Renderer.Render(Request("vi", privacy: privacy)).Value.Text.ShouldContain(label);
        }
    }

    /// <summary>The English sentence carries the same covered values through the English label maps.</summary>
    [Fact]
    public void EnglishText_ContainsAllCoveredFields()
    {
        var rendered = Renderer.Render(Request("en")).Value;
        string[] expected =
        [
            "Midnight Files",
            "ghost stories",
            "Night Tales Channel",
            "Mon, Thu",
            "19:00",
            "Asia/Ho_Chi_Minh",
            "2",
            "unlisted (link only)",
            "6",
        ];

        foreach (var value in expected)
        {
            rendered.Text.ShouldContain(value);
        }

        Renderer.Render(Request("en", topic: Topic.TrueCrime)).Value.Text
            .ShouldContain("true crime (real people, always reviewed by me)");
        Renderer.Render(Request("en", privacy: PrivacyStatus.Public)).Value.Text.ShouldContain("public (everyone)");
        Renderer.Render(Request("en", privacy: PrivacyStatus.Private)).Value.Text.ShouldContain("private");
    }

    /// <summary>
    /// The weekday list is localised and always reads Monday-first, whatever order the stored cadence kept, with a
    /// repeated day listed once.
    /// </summary>
    [Fact]
    public void DaysOfWeek_ReadsMondayFirst()
    {
        DayOfWeek[] days = [DayOfWeek.Sunday, DayOfWeek.Thursday, DayOfWeek.Monday, DayOfWeek.Thursday];

        Renderer.Render(Request("en", days: days)).Value.Text.ShouldContain("Mon, Thu, Sun");
        Renderer.Render(Request("vi", days: days)).Value.Text.ShouldContain("Thứ Hai, Thứ Năm, Chủ Nhật");
    }

    /// <summary>
    /// The series title is user text: a title that carries a placeholder's characters reaches the owner as those
    /// characters, never as a value a second substitution pass put there.
    /// </summary>
    [Fact]
    public void SeriesTitleWithPlaceholderText_IsInsertedLiterally()
    {
        var rendered = Renderer.Render(Request("en", seriesTitle: "Night {{daily_cap}} Files")).Value;

        rendered.Text.ShouldContain("\"Night {{daily_cap}} Files\"");
        // The real daily cap is still the one the settings say, in its own slot.
        rendered.Text.ShouldContain("at most **2** video(s) per day");
        rendered.Text.ShouldContain("{{daily_cap}}");
    }

    /// <summary>
    /// Only the join at the note is normalised: a double space inside the title is the owner's text and survives
    /// verbatim.
    /// </summary>
    [Fact]
    public void SeriesTitleWithDoubleSpace_IsKeptVerbatim()
    {
        var rendered = Renderer.Render(Request("en", seriesTitle: "Night  Files")).Value;

        rendered.Text.ShouldContain("\"Night  Files\"");
    }

    /// <summary>A cadence time is a covered field: a different time renders different words and a different digest.</summary>
    [Fact]
    public void DifferentPublishTime_ChangesHash()
    {
        var evening = Renderer.Render(Request("en", publishTime: new TimeOnly(19, 0))).Value;
        var morning = Renderer.Render(Request("en", publishTime: new TimeOnly(7, 30))).Value;

        evening.Text.ShouldContain("at 19:00");
        morning.Text.ShouldContain("at 07:30");
        morning.TextHash.ShouldNotBe(evening.TextHash);
    }

    /// <summary>
    /// A locale that is not one of the two the templates exist for is refused on the consent query's own shape: a
    /// <c>validation_failed</c> error on field <c>locale</c>, never an English answer as a silent default.
    /// </summary>
    /// <param name="locale">The refused wire locale.</param>
    [Theory]
    [InlineData("fr")]
    [InlineData("VI")]
    [InlineData("")]
    public void UnknownLocale_ReturnsAnError(string locale)
    {
        var result = Renderer.Render(Request(locale));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.FirstError.Description.ShouldBe("locale_unknown");
        result.FirstError.Metadata![ErrorMetadata.Field].ShouldBe("locale");
    }

    /// <summary>The contract's two static members: the wire locales and the current text version.</summary>
    [Fact]
    public void Locales_AreTheTwoWireLocales()
    {
        AuthorizationTextRenderer.Locales.ShouldBe(["vi", "en"]);
        AuthorizationTextRenderer.CurrentTextVersion.ShouldBe("autopilot-auth.v1");
    }

    /// <summary>A request with every covered value but the one a test varies.</summary>
    /// <param name="locale">The wire locale.</param>
    /// <param name="preAuditPending">Whether the channel's YouTube audit is still pending.</param>
    /// <param name="seriesTitle">The series title.</param>
    /// <param name="topic">The series topic.</param>
    /// <param name="privacy">The stored privacy.</param>
    /// <param name="days">The cadence's weekdays; <see cref="BusinessWeek"/> by default.</param>
    /// <param name="publishTime">The cadence's local publish time; 19:00 by default.</param>
    /// <returns>The request.</returns>
    private static AuthorizationTextRequest Request(
        string locale,
        bool preAuditPending = false,
        string seriesTitle = "Midnight Files",
        Topic topic = Topic.Ghost,
        PrivacyStatus privacy = PrivacyStatus.Unlisted,
        DayOfWeek[]? days = null,
        TimeOnly? publishTime = null) =>
        new(
            locale,
            seriesTitle,
            topic,
            "Night Tales Channel",
            new AutopilotSettings(
                SchemaVersion: 1,
                Cadence: new AutopilotCadence(
                    days ?? BusinessWeek,
                    publishTime ?? new TimeOnly(19, 0),
                    "Asia/Ho_Chi_Minh"),
                DailyCap: 2,
                BufferDays: 2,
                Privacy: privacy,
                VetoWindowHours: 6,
                PreAuditMode: PreAuditMode.KeepCadencePrivate,
                PreAuditPauseAfter: 3,
                GateThresholdsRef: "default",
                Tone: "slow-burn dread",
                BannedThemes: ["graphic violence"],
                RenderLeadMinutes: null,
                StoryLeadMinutes: null),
            preAuditPending);

    /// <summary>The pre-audit note the task's 'Authorization text' section gives for one locale.</summary>
    /// <param name="locale">The wire locale.</param>
    /// <returns>The note sentence.</returns>
    private static string PreAuditNote(string locale) => locale == "vi"
        ? "Cho đến khi YouTube duyệt API, các video chỉ ở chế độ **riêng tư** và không được công khai."
        : "Until the YouTube API audit is approved, uploads stay **private** and are not published publicly.";

    /// <summary>The digest an independent implementation computes over the text, the task's formula in one line.</summary>
    /// <param name="text">The text the renderer returned.</param>
    /// <returns><c>sha256(UTF-8(NFC(text)))</c> in lower-case hex.</returns>
    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC))));
}
