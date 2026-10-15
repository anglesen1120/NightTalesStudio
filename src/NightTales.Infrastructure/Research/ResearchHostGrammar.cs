namespace NightTales.Infrastructure.Research;

/// <summary>
/// The canonical host grammar shared by the start-up check (AL2) and the lookup (<see cref="EmbeddedResearchAllowList"/>),
/// so both accept exactly the same hosts (02.7.2-T1).
/// </summary>
internal static class ResearchHostGrammar
{
    /// <summary>The longest host, in ASCII characters.</summary>
    public const int MaxHostLength = 253;

    /// <summary>
    /// True when <paramref name="host"/> is a canonical host: at most 253 characters, at least one dot, and every label
    /// 1–63 characters of lowercase ASCII letters, digits and hyphens with no leading or trailing hyphen.
    /// </summary>
    public static bool IsCanonicalHost(string host)
    {
        if (host.Length is 0 or > MaxHostLength || !host.Contains('.'))
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
