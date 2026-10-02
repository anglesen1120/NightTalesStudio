using NightTales.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Storage;

/// <summary>
/// The header builder of a presigned download (09.7.2-T1 acceptance "safe header", BD16): a title can
/// carry a quote, a backslash or a CR/LF pair, and none of them may reach a response header, while the
/// name a user sees stays recognisable.
/// </summary>
public sealed class SafeContentDispositionTests
{
    [Fact]
    public void SafeAttachment_StripsQuotesBackslashesAndNewlines()
    {
        var disposition = SafeContentDisposition.Attachment("Tập \"một\\\r\n.mp4")!;

        disposition.ShouldNotBeNull();

        // A header value may never carry CR or LF at all: that is the injection boundary.
        disposition.ShouldNotContain("\r", Case.Sensitive);
        disposition.ShouldNotContain("\n", Case.Sensitive);

        var (ascii, extended) = Parameters(disposition);

        ascii.ShouldNotContain("\"", Case.Sensitive);
        ascii.ShouldNotContain("\\", Case.Sensitive);
        ascii.ShouldNotContain("\r", Case.Sensitive);
        ascii.ShouldNotContain("\n", Case.Sensitive);
        ascii.ShouldNotContain("\t", Case.Sensitive);
        ascii.ShouldBe("Tap _mot_.mp4", "the safe characters survive; the dangerous ones become underscores");

        // The UTF-8 parameter is percent-encoded, so a dangerous character can only appear escaped.
        extended.ShouldNotContain("\"", Case.Sensitive);
        extended.ShouldNotContain("\\", Case.Sensitive);
        extended.ShouldNotContain("\r", Case.Sensitive);
        extended.ShouldNotContain("\n", Case.Sensitive);
        extended.ShouldNotContain("'", Case.Sensitive);
        extended.ShouldContain("%22", Case.Sensitive, "the quote must be percent-encoded, not dropped");
        extended.ShouldContain("%5C", Case.Sensitive, "the backslash must be percent-encoded");
        extended.ShouldContain("%0D", Case.Sensitive, "CR must be percent-encoded");
    }

    [Fact]
    public void SafeAttachment_IsAsciiOnly_AndAtMost80Chars()
    {
        var name = string.Concat(Enumerable.Repeat("Tập một chuyện dài ", 40)) + ".mp4";

        var disposition = SafeContentDisposition.Attachment(name)!;

        disposition.ShouldNotBeNull();
        disposition.All(char.IsAscii).ShouldBeTrue($"'{disposition}' must be ASCII (09.7.2-T1 WF16)");

        var (ascii, extended) = Parameters(disposition);

        ascii.All(char.IsAscii).ShouldBeTrue($"the ASCII parameter '{ascii}' must be ASCII");
        extended.All(char.IsAscii).ShouldBeTrue($"the ext-value '{extended}' must be ASCII");
        ascii.Length.ShouldBeLessThanOrEqualTo(SafeContentDisposition.MaxFileNameLength);
        ascii.ShouldNotBeEmpty();
        ascii.ShouldNotEndWith("_");
        ascii.ShouldNotEndWith(" ");
    }

    [Fact]
    public void SafeAttachment_ReturnsNullForNullName()
    {
        SafeContentDisposition.Attachment(null).ShouldBeNull();
        SafeContentDisposition.Attachment(string.Empty).ShouldBeNull();
        SafeContentDisposition.Attachment("   ").ShouldBeNull();
    }

    [Fact]
    public void SafeAttachment_UsesAFallback_WhenNothingSurvivesTheFold()
    {
        // A non-blank name whose every character the fold drops (a control character folds to `_`, which
        // the trim then removes): the ASCII parameter still has to be a name, never empty, and the
        // UTF-8 parameter still describes what was asked for. A blank name is a different case and
        // answers null (SafeAttachment_ReturnsNullForNullName).
        var (ascii, extended) = Parameters(SafeContentDisposition.Attachment("\u0007")!);

        ascii.ShouldBe(SafeContentDisposition.FallbackFileName);
        extended.ShouldNotBeEmpty();
    }

    /// <summary>
    /// Splits the header value into its two filename parameters — the ASCII one between
    /// <c>filename="</c> and <c>"</c>, and the RFC 5987 <c>ext-value</c> after <c>UTF-8''</c> — so the
    /// assertions are about the parameters rather than about the delimiters that must be there.
    /// </summary>
    private static (string Ascii, string Extended) Parameters(string disposition)
    {
        disposition.ShouldStartWith("attachment; filename=\"");
        disposition.ShouldContain("filename*=UTF-8''");

        var asciiStart = disposition.IndexOf('"', StringComparison.Ordinal) + 1;
        var asciiEnd = disposition.IndexOf('"', asciiStart);
        var extendedStart = disposition.IndexOf("UTF-8''", StringComparison.Ordinal) + "UTF-8''".Length;

        return (disposition[asciiStart..asciiEnd], disposition[extendedStart..]);
    }
}
