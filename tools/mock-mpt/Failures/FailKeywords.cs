using NightTales.MockMpt.Fixtures;
using NightTales.RenderNode.Contract;

namespace NightTales.MockMpt.Failures;

/// <summary>What the trigger keywords of one submit selected (11.1.2-T2 Design).</summary>
/// <param name="Mode">The mode a <c>__fail_&lt;mode&gt;__</c> keyword selected, or null.</param>
/// <param name="FailImage">True when <c>__fail_image__</c> is present: <c>moderated_all</c> for this submit.</param>
/// <param name="BlockedSlots">The <c>video_terms</c> indexes that carry <c>__block_image__</c>.</param>
public sealed record KeywordHits(FailMode? Mode, bool FailImage, IReadOnlySet<int> BlockedSlots)
{
    /// <summary>No keyword present.</summary>
    public static KeywordHits Empty { get; } = new(null, false, new HashSet<int>());

    /// <summary>
    /// True when a failure keyword decided (the precedence layer 1). <c>__block_image__</c> only shapes image
    /// slots, so a lone one does not suppress <c>MOCK_FAIL_MODE</c>/<c>MOCK_FAIL_RATE</c>.
    /// </summary>
    public bool Any => Mode is not null || FailImage;
}

/// <summary>
/// The trigger-keyword layer (11.1.2-T2 Design): <c>__fail_image__</c>, <c>__block_image__</c> and
/// <c>__fail_&lt;mode&gt;__</c>, searched in the free-text fields of the submit body (<c>video_script</c>,
/// <c>video_terms</c>), case-sensitive and whole-token. An unknown name is ignored — a submit can never fail the
/// process. <c>__fixture_&lt;name&gt;__</c> is <see cref="FixtureKeyword"/>'s.
/// </summary>
public static class FailKeywords
{
    /// <summary>The <c>__fail_&lt;name&gt;__</c> token prefix.</summary>
    public const string FailPrefix = "__fail_";

    /// <summary>The slot keyword: the <c>video_terms</c> entry carrying it becomes a moderated slot.</summary>
    public const string BlockImage = "__block_image__";

    private const string Suffix = "__";
    private const string ImageName = "image";

    /// <summary>Scans <paramref name="mpt"/>'s <c>video_script</c> and <c>video_terms</c>.</summary>
    public static KeywordHits Scan(MptSubmitFields mpt, FixtureSet fixtures)
    {
        ArgumentNullException.ThrowIfNull(mpt);
        ArgumentNullException.ThrowIfNull(fixtures);

        FailMode? mode = null;
        var failImage = false;
        ScanFail(mpt.VideoScript, fixtures, ref mode, ref failImage);

        var blocked = new HashSet<int>();
        for (var slot = 0; slot < mpt.VideoTerms.Count; slot++)
        {
            var term = mpt.VideoTerms[slot];
            if (term is null)
            {
                continue;
            }

            ScanFail(term, fixtures, ref mode, ref failImage);
            if (ContainsWholeToken(term, BlockImage))
            {
                blocked.Add(slot);
            }
        }

        return new KeywordHits(mode, failImage, blocked);
    }

    private static void ScanFail(string text, FixtureSet fixtures, ref FailMode? mode, ref bool failImage)
    {
        if (text is null)
        {
            return;
        }

        var start = text.IndexOf(FailPrefix, StringComparison.Ordinal);
        while (start >= 0)
        {
            var nameStart = start + FailPrefix.Length;
            var end = text.IndexOf(Suffix, nameStart, StringComparison.Ordinal);
            if (end > nameStart && IsName(text.AsSpan(nameStart, end - nameStart)) &&
                IsWholeToken(text, start, end + Suffix.Length))
            {
                var name = text[nameStart..end];
                if (string.Equals(name, ImageName, StringComparison.Ordinal))
                {
                    failImage = true;
                }
                else if (mode is null && FailModeParser.FromKeyword(name, fixtures) is { } found)
                {
                    mode = found;
                }
            }

            start = text.IndexOf(FailPrefix, nameStart, StringComparison.Ordinal);
        }
    }

    private static bool IsName(ReadOnlySpan<char> candidate)
    {
        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whole-token test: the match touches a string edge or a non-token character on both sides. A token
    /// character is an ASCII letter, digit or <c>_</c>, so <c>pre__fail_x__post</c> is one token and is ignored.
    /// </summary>
    private static bool IsWholeToken(string text, int start, int end) =>
        (start == 0 || !IsTokenChar(text[start - 1])) &&
        (end >= text.Length || !IsTokenChar(text[end]));

    private static bool IsTokenChar(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

    private static bool ContainsWholeToken(string text, string keyword)
    {
        var at = text.IndexOf(keyword, StringComparison.Ordinal);
        while (at >= 0)
        {
            if (IsWholeToken(text, at, at + keyword.Length))
            {
                return true;
            }

            at = text.IndexOf(keyword, at + 1, StringComparison.Ordinal);
        }

        return false;
    }
}
