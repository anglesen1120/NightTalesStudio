namespace NightTales.Domain.Stories.Keywords;

/// <summary>
/// The R6 list <see cref="KeywordRules"/> rejects (02 README D3, 02 T78). The categories are fixed by R6 —
/// gore, blood, injury, corpse or dead body, weapon, self-harm or suicide, nudity or sexual content, and a
/// child or minor as the subject — and the entries are a proposal the owner may extend; the list is data,
/// so extending it changes no code.
/// </summary>
public static class GoreTerms
{
    /// <summary>
    /// The R6 minor terms a keyword may only use through an object the minor left ("child's drawing"),
    /// never as the subject; <see cref="KeywordRules"/> applies the R6 permission to a possessive of a
    /// term of this set (02 T78, keyword-rules.v1.md). The apostrophe-less possessive/plural forms are
    /// listed too, so canonicalising a keyword cannot hide a minor behind a missing apostrophe.
    /// </summary>
    public static readonly IReadOnlySet<string> MinorSubjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "child", "childs", "minor", "minors", "boy", "boys", "girl", "girls", "kid", "kids",
    };

    /// <summary>The rejected terms, matched case-insensitively as whole tokens (a multi-word entry too).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // gore, blood, injury
        "gore", "blood", "bloody", "bleeding", "wound", "wounded", "injury", "injured", "bruise",

        // corpse or dead body
        "corpse", "cadaver", "dead body", "dead", "death",

        // weapon, including the hanging/rope imagery R6 names
        "knife", "blade", "gun", "pistol", "weapon", "rope", "noose", "hanging", "hanged",

        // self-harm or suicide
        "suicide", "self-harm", "overdose",

        // nudity or sexual content
        "naked", "nude", "nudity", "sexual", "sex",

        // a minor as the subject of a keyword
        "child", "childs", "minor", "minors", "boy", "boys", "girl", "girls", "kid", "kids",
    };
}
