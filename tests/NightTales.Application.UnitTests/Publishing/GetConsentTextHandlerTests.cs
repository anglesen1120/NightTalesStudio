using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Queries.GetConsentText;
using NightTales.Application.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// <c>GET /api/v1/publications/consent-text?locale=vi|en</c> (06.2.2-T1, 06 README D7; 10 CR-17): the two embedded
/// <c>pc.v1</c> texts, the version read from the live option rather than a constant, and the one refusal the validator
/// and the handler both answer for every other locale.
/// </summary>
/// <remarks>
/// No database and no doubles: the query reads only the assembly's embedded resources and the bound options, so the
/// tests construct the handler directly and pin the real template bytes — a resource the build stops embedding fails
/// here, not in a deployment.
/// </remarks>
public sealed class GetConsentTextHandlerTests
{
    /// <summary>The default <c>YouTube:ConsentTextVersion</c> (06 README D11).</summary>
    private const string DefaultVersion = "pc.v1";

    /// <summary>The compliance marker every template ends with (the last line of the file).</summary>
    private const string ReviewMarker = "<!-- legal wording: owner/13 compliance review pending -->";

    /// <summary>The request field a locale refusal answers on.</summary>
    private const string LocaleField = "locale";

    /// <summary>The per-field wire string the validator and the handler both answer (validator codes, not copy).</summary>
    private const string LocaleUnknownMessage = "locale_unknown";

    [Theory]
    [InlineData("fr")]
    [InlineData("VI")]
    [InlineData("EN")]
    [InlineData("vi-VN")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task UnknownLocale_IsAValidationErrorOnTheLocaleField(string locale)
    {
        var result = await HandleAsync(locale);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.FirstError.Description.ShouldBe(LocaleUnknownMessage);
        result.FirstError.Metadata!["field"].ShouldBe(LocaleField);
    }

    [Fact]
    public void Validator_AcceptsOnlyTheTwoEmbeddedLocales()
    {
        var validator = new GetConsentTextValidator();

        foreach (var locale in new[] { "vi", "en" })
        {
            validator.Validate(new GetConsentTextQuery(locale)).IsValid.ShouldBeTrue(locale);
        }

        foreach (var locale in new[] { "fr", "VI", "EN", "vi-VN", "", " " })
        {
            var failure = validator.Validate(new GetConsentTextQuery(locale));

            failure.IsValid.ShouldBeFalse(locale);
            var error = failure.Errors.ShouldHaveSingleItem();
            error.PropertyName.ShouldBe("Locale");
            error.ErrorMessage.ShouldBe(LocaleUnknownMessage);
        }
    }

    [Fact]
    public async Task Vietnamese_ReturnsTheEmbeddedVietnameseText()
    {
        var locale = await HandleAsync("vi");

        locale.IsError.ShouldBeFalse();
        locale.Value.Text.ShouldNotBeNullOrWhiteSpace();
        locale.Value.Text.ShouldStartWith("# ");
        locale.Value.Text.ShouldContain("Night Tales Studio");
        locale.Value.Text.ShouldContain("YouTube");
        locale.Value.Text.ShouldContain("Điều khoản dịch vụ của YouTube");
        locale.Value.Text.ShouldNotContain(DefaultVersion);
        locale.Value.Text.TrimEnd().ShouldEndWith(ReviewMarker);
    }

    [Fact]
    public async Task English_ReturnsItsOwnTextAndTheSameVersion()
    {
        var vietnamese = await HandleAsync("vi");
        var english = await HandleAsync("en");

        english.IsError.ShouldBeFalse();
        english.Value.Text.ShouldNotBeNullOrWhiteSpace();
        english.Value.Text.ShouldStartWith("# ");
        english.Value.Text.ShouldContain("Night Tales Studio");
        english.Value.Text.ShouldContain("Terms of Service");
        english.Value.Text.ShouldNotContain(DefaultVersion);
        english.Value.Text.TrimEnd().ShouldEndWith(ReviewMarker);

        // Two distinct templates, one version: the version names the text revision, not a locale.
        english.Value.Text.ShouldNotBe(vietnamese.Value.Text);
        english.Value.Version.ShouldBe(vietnamese.Value.Version);
    }

    [Fact]
    public async Task Version_IsTheConfiguredOptionValue_NotAConstant()
    {
        var bumped = await HandleAsync("vi", version: "pc.v2");
        var configuredDefault = await HandleAsync("en");

        bumped.IsError.ShouldBeFalse();
        bumped.Value.Version.ShouldBe("pc.v2");
        configuredDefault.Value.Version.ShouldBe(DefaultVersion);
    }

    /// <summary>Runs the handler over one locale with a bound <c>YouTube</c> section of the given version.</summary>
    /// <param name="locale">The query's wire locale.</param>
    /// <param name="version">The <c>YouTube:ConsentTextVersion</c> the handler must report.</param>
    /// <returns>The handler's answer.</returns>
    private static async Task<ErrorOr<ConsentTextDto>> HandleAsync(string locale, string version = DefaultVersion)
    {
        var handler = new GetConsentTextHandler(new YouTubeOptions { ConsentTextVersion = version });

        return await handler.Handle(new GetConsentTextQuery(locale), CancellationToken.None);
    }
}
