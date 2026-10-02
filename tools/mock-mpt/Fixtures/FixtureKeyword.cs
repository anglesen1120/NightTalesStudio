using NightTales.RenderNode.Contract;

namespace NightTales.MockMpt.Fixtures;

/// <summary>
/// The <c>__fixture_&lt;name&gt;__</c> trigger of 11.1.2-T1's Design: a submit whose free text carries it
/// replays that D14 fixture's state instead of walking the normal progress clock. The scan is case-sensitive
/// and whole-token; an unknown name is ignored (the mock's normal path runs), because a submit can never fail
/// the process.
/// </summary>
public static class FixtureKeyword
{
    /// <summary>The token prefix.</summary>
    public const string Prefix = "__fixture_";

    private const string Suffix = "__";

    /// <summary>The fixture id in <paramref name="mpt"/>'s free text, or null.</summary>
    public static string? Find(MptSubmitFields mpt)
    {
        ArgumentNullException.ThrowIfNull(mpt);

        return Find(mpt.VideoScript) ?? FindIn(mpt.VideoTerms);
    }

    /// <summary>The first <c>__fixture_&lt;name&gt;__</c> token in one string, or null.</summary>
    public static string? Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var start = text.IndexOf(Prefix, StringComparison.Ordinal);
        while (start >= 0)
        {
            var nameStart = start + Prefix.Length;
            var end = text.IndexOf(Suffix, nameStart, StringComparison.Ordinal);
            if (end > nameStart)
            {
                var name = text[nameStart..end];
                if (IsFixtureId(name))
                {
                    return name;
                }
            }

            start = text.IndexOf(Prefix, nameStart, StringComparison.Ordinal);
        }

        return null;
    }

    private static string? FindIn(IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            if (term is not null && Find(term) is { } name)
            {
                return name;
            }
        }

        return null;
    }

    private static bool IsFixtureId(string candidate)
    {
        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return candidate.Length > 0;
    }
}
