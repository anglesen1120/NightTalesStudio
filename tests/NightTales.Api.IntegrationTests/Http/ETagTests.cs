using NightTales.Application.Common;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// The ETag format and the <c>If-Match</c> comparison of 09 README D15 / 02 README D9. A weak tag or a
/// star must never validate, or a caller could overwrite a row it never read.
/// </summary>
public sealed class ETagTests
{
    [Fact]
    public void ETags_Matches_IsStrongExact()
    {
        var current = ETags.Episode(1);
        current.ShouldBe("\"ep-1\"");

        ETags.Matches("\"ep-1\"", current).ShouldBeTrue();
        ETags.Matches("*", current).ShouldBeFalse("a star is not accepted [DECISION]");
        ETags.Matches("W/\"ep-1\"", current).ShouldBeFalse("a weak tag never validates a lost-update check");
        ETags.Matches("w/\"ep-1\"", current).ShouldBeFalse();
        ETags.Matches("\"EP-1\"", current).ShouldBeFalse("the comparison is ordinal");
        ETags.Matches("\"ep-2\"", current).ShouldBeFalse();
        ETags.Matches("\"ep-1\", \"ep-2\"", current).ShouldBeFalse("a client sends the one tag it read");
        ETags.Matches(null, current).ShouldBeFalse();
        ETags.Matches(string.Empty, current).ShouldBeFalse();
        ETags.Matches(" \"ep-1\"", current).ShouldBeFalse("the header is compared byte for byte");

        ETags.Series(3).ShouldBe("\"se-3\"");
        ETags.Publication(9).ShouldBe("\"pu-9\"");
        ETags.Matches("\"se-3\"", ETags.Series(3)).ShouldBeTrue();
        ETags.Matches("\"se-3\"", ETags.Episode(3)).ShouldBeFalse("the prefixes are not interchangeable");
    }
}
