using NightTales.Application.Features.Research.Commands.FindSources;
using NightTales.Domain.Stories.Generation;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Research.FindSources;

/// <summary>
/// 02.7.2-T2: the shape validation of <c>POST /episodes/{id}/sources/find</c> — the 300-character query bound applied
/// after trimming and control-character stripping (D19/plan §3), and the <c>Idempotency-Key</c> contract of the
/// command a job or an autopilot caller sends without passing the route filter.
/// </summary>
public sealed class FindSourcesCommandValidatorTests
{
    private static readonly Guid EpisodeId = Guid.Parse("0197a000-0000-7000-8000-0000000000b1");

    private static readonly FindSourcesCommandValidator Validator = new();

    [Fact]
    public void FindSourcesValidator_QueryExactly300_IsValid()
    {
        Validator.Validate(Command(new string('q', FindSourcesCommand.MaxQueryLength))).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void FindSourcesValidator_QueryOver300_Fails()
    {
        var result = Validator.Validate(Command(new string('q', FindSourcesCommand.MaxQueryLength + 1)));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe("query_too_long");
    }

    [Fact]
    public void FindSourcesValidator_QueryAt300AfterTrim_IsValid()
    {
        // The bound is on the stored value: four leading spaces must not make a 300-character query invalid.
        var raw = "    " + new string('q', FindSourcesCommand.MaxQueryLength);

        Validator.Validate(Command(raw)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void FindSourcesValidator_ControlCharactersDoNotCountTowardTheBound()
    {
        // 300 letters plus control/format/line-separator noise is still a 300-character stored query.
        var raw = string.Concat(new string('q', 150), "\u0000\u200B\u2028\t", new string('q', 150));

        Validator.Validate(Command(raw)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void FindSourcesValidator_RawQueryPastTheRawCap_FailsBeforeNormalising()
    {
        // 2001 raw characters that would strip down to one stored character: refused on the raw length alone, so a
        // request-body-sized query never reaches the stripping allocation.
        var raw = string.Concat(new string('\u200B', FindSourcesCommand.RawQueryMaxChars), "q");

        var result = Validator.Validate(Command(raw));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.ErrorMessage == "query_too_long");
    }

    [Fact]
    public void FindSourcesValidator_RawQueryAtTheRawCapWithNoiseOnly_IsValid()
    {
        var raw = string.Concat(new string('q', FindSourcesCommand.MaxQueryLength), new string('\u200B', FindSourcesCommand.RawQueryMaxChars - FindSourcesCommand.MaxQueryLength));

        raw.Length.ShouldBe(FindSourcesCommand.RawQueryMaxChars);
        Validator.Validate(Command(raw)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void FindSourcesCommand_ToString_NeverPrintsTheQuery()
    {
        const string Marker = "qry-marker-7f3a9c";

        Command(Marker).ToString().ShouldNotContain(Marker);
    }

    [Fact]
    public void FindSourcesValidator_NoQuery_IsValid()
    {
        Validator.Validate(Command(null)).IsValid.ShouldBeTrue("the job derives the query when the caller types none");
        Validator.Validate(Command("   ")).IsValid.ShouldBeTrue("whitespace normalises to null");
    }

    [Fact]
    public void FindSourcesValidator_EmptyEpisode_Fails()
    {
        var result = Validator.Validate(
            new FindSourcesCommand(Guid.Empty, null, GenerationTrigger.User, "find-1"));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(FindSourcesCommand.EpisodeId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("with\ttab")]
    [InlineData("non-ascii-é")]
    public void FindSourcesValidator_InvalidKey_Fails(string? key)
    {
        Validator.Validate(new FindSourcesCommand(EpisodeId, null, GenerationTrigger.User, key))
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void FindSourcesValidator_KeyLengthBounds_AreTheColumnContract()
    {
        Validator.Validate(
                new FindSourcesCommand(EpisodeId, null, GenerationTrigger.User, new string('k', FindSourcesCommandValidator.MaxKeyLength)))
            .IsValid.ShouldBeTrue();

        Validator.Validate(
                new FindSourcesCommand(EpisodeId, null, GenerationTrigger.User, new string('k', FindSourcesCommandValidator.MaxKeyLength + 1)))
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void NormalizeQuery_TrimsAndStripsControlFormatAndLineSeparators()
    {
        FindSourcesCommand.NormalizeQuery("  hello world  ").ShouldBe("hello world");
        FindSourcesCommand.NormalizeQuery("a\u0000b\u200Bc\u2028d\u2029e").ShouldBe("abcde");
        FindSourcesCommand.NormalizeQuery(" \t\r\n ").ShouldBeNull();
        FindSourcesCommand.NormalizeQuery(string.Empty).ShouldBeNull();
        FindSourcesCommand.NormalizeQuery(null).ShouldBeNull();
        FindSourcesCommand.NormalizeQuery("giữ nguyên tiếng Việt").ShouldBe("giữ nguyên tiếng Việt");
    }

    private static FindSourcesCommand Command(string? query) =>
        new(EpisodeId, query, GenerationTrigger.User, "find-1");
}
