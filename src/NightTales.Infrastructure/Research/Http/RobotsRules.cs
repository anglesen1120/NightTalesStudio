using System.Globalization;

namespace NightTales.Infrastructure.Research.Http;

/// <summary>
/// The parsed robots.txt rules of one crawler token (RFC 9309 subset of the 02.5.2-T0 spike §2): groups, exact-token
/// group over the <c>*</c> group, longest match, <c>allow</c> wins ties, <c>*</c> and a trailing <c>$</c>, an empty
/// <c>disallow</c> allows everything, unknown directives are ignored, at most <see cref="MaxRules"/> rules, and
/// <c>crawl-delay</c> is read (the caller applies the floor and the cap).
/// </summary>
public sealed class RobotsRules
{
    /// <summary>The most rules kept for the selected group; the rest are dropped (a permissive degradation).</summary>
    public const int MaxRules = 100;

    /// <summary>
    /// A rule value longer than this is ignored (a permissive degradation). With <see cref="MaxRules"/> it bounds the
    /// matcher's worst case, which is O(rules × pattern × path) per decision (02.5.2-T1 review F4).
    /// </summary>
    public const int MaxRuleLength = 256;

    private readonly Rule[] _rules;

    private RobotsRules(Rule[] rules, TimeSpan? crawlDelay, bool ruleLimitExceeded)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
        RuleLimitExceeded = ruleLimitExceeded;
    }

    /// <summary>Rules that allow everything and carry no crawl delay (no group applies, or the file is empty).</summary>
    public static RobotsRules AllowAll { get; } = new([], null, false);

    /// <summary>The <c>crawl-delay</c> of the selected group, or <see langword="null"/> when absent or malformed.</summary>
    public TimeSpan? CrawlDelay { get; }

    /// <summary>Whether the selected group had more than <see cref="MaxRules"/> rules and the surplus was dropped.</summary>
    public bool RuleLimitExceeded { get; }

    /// <summary>Parses <paramref name="content"/> for the crawler <paramref name="userAgentToken"/>.</summary>
    /// <param name="content">The decoded robots.txt (a leading BOM is stripped).</param>
    /// <param name="userAgentToken">The crawler token, matched case-insensitively against <c>user-agent</c> lines.</param>
    /// <returns>The rules of the selected group.</returns>
    public static RobotsRules Parse(string content, string userAgentToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(userAgentToken);

        var groups = ReadGroups(content);

        var exact = groups.Where(group => group.Agents.Contains(userAgentToken, StringComparer.OrdinalIgnoreCase)).ToList();
        var selected = exact.Count > 0 ? exact : groups.Where(group => group.Agents.Contains("*", StringComparer.Ordinal)).ToList();
        if (selected.Count == 0)
        {
            return AllowAll;
        }

        var rules = selected.SelectMany(group => group.Rules).ToList();
        var exceeded = rules.Count > MaxRules;
        if (exceeded)
        {
            rules.RemoveRange(MaxRules, rules.Count - MaxRules);
        }

        var delay = selected.Select(group => group.CrawlDelay).FirstOrDefault(value => value is not null);

        return new RobotsRules([.. rules], delay, exceeded);
    }

    /// <summary>Whether <paramref name="pathAndQuery"/> may be fetched.</summary>
    /// <param name="pathAndQuery">The request path followed by its query, for example <c>/a/b?x=1</c>.</param>
    /// <returns>
    /// <see langword="false"/> when the longest matching rule is a <c>disallow</c>; ties go to <c>allow</c>; no match allows.
    /// </returns>
    public bool IsAllowed(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);

        var target = Decode(pathAndQuery);
        if (target.Length == 0 || target[0] != '/')
        {
            target = "/" + target;
        }

        Rule? best = null;
        foreach (var rule in _rules)
        {
            if (!Matches(rule, target))
            {
                continue;
            }

            if (best is null || rule.Pattern.Length > best.Pattern.Length || (rule.Pattern.Length == best.Pattern.Length && rule.Allow && !best.Allow))
            {
                best = rule;
            }
        }

        return best is null || best.Allow;
    }

    private static List<Group> ReadGroups(string content)
    {
        var groups = new List<Group>();
        Group? current = null;
        var lastWasAgent = false;

        foreach (var rawLine in content.TrimStart('\uFEFF').Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                line = line[..hash];
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].TrimStart();

            switch (field)
            {
                case "user-agent":
                    if (!lastWasAgent || current is null)
                    {
                        current = new Group();
                        groups.Add(current);
                    }

                    current.Agents.Add(value.Trim());
                    lastWasAgent = true;
                    break;

                case "allow" or "disallow":
                    if (current is not null)
                    {
                        current.Add(field == "allow", value);
                    }

                    lastWasAgent = false;
                    break;

                case "crawl-delay":
                    if (current is not null && current.CrawlDelay is null && TryParseDelay(value.Trim(), out var delay))
                    {
                        current.CrawlDelay = delay;
                    }

                    lastWasAgent = false;
                    break;

                default:
                    // sitemap, host, request-rate, visit-time and every unknown field are ignored (their values are not
                    // read), but they end the run of consecutive user-agent lines: a later user-agent starts a new group (T0 §2).
                    lastWasAgent = false;
                    break;
            }
        }

        return groups;
    }

    private static bool TryParseDelay(string value, out TimeSpan delay)
    {
        delay = default;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || double.IsNaN(seconds)
            || double.IsInfinity(seconds)
            || seconds < 0
            || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return false;
        }

        delay = TimeSpan.FromSeconds(seconds);

        return true;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value);

    private static bool Matches(Rule rule, string target)
    {
        var pattern = rule.Body;
        var anchored = rule.Anchored;
        var patternIndex = 0;
        var targetIndex = 0;
        var starPattern = -1;
        var starTarget = 0;

        while (true)
        {
            if (patternIndex == pattern.Length)
            {
                if (!anchored || targetIndex == target.Length)
                {
                    return true;
                }
            }
            else if (pattern[patternIndex] == '*')
            {
                starPattern = patternIndex++;
                starTarget = targetIndex;
                continue;
            }
            else if (targetIndex < target.Length && pattern[patternIndex] == target[targetIndex])
            {
                patternIndex++;
                targetIndex++;
                continue;
            }

            if (starPattern < 0 || starTarget >= target.Length)
            {
                return false;
            }

            starTarget++;
            targetIndex = starTarget;
            patternIndex = starPattern + 1;
        }
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];

        public List<Rule> Rules { get; } = [];

        public TimeSpan? CrawlDelay { get; set; }

        public void Add(bool allow, string rawValue)
        {
            var value = Decode(rawValue);
            if (value.Length == 0 || value.Length > MaxRuleLength)
            {
                // An empty allow/disallow matches nothing; an oversized pattern is ignored.
                return;
            }

            var anchored = value[^1] == '$';
            var pattern = anchored ? value[..^1] : value;
            if (pattern.Length == 0 || (pattern[0] != '/' && pattern[0] != '*'))
            {
                pattern = "/" + pattern;
            }

            Rules.Add(new Rule(allow, anchored ? pattern + "$" : pattern, pattern, anchored));
        }
    }

    /// <summary><paramref name="Pattern"/> is the rule value (its length ranks the match); <paramref name="Body"/> omits the <c>$</c> anchor.</summary>
    private sealed record Rule(bool Allow, string Pattern, string Body, bool Anchored);
}
