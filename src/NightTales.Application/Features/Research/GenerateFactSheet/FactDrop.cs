namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// One fact the validator removed from the model output (02.5.3-T2), with the ordered rule that removed it
/// and its position in the output. A drop is data — never an error — so the run detail and the UI can explain
/// why a fact the model produced is missing.
/// </summary>
/// <param name="Reason">One of the <see cref="FactDropReasons"/> constants.</param>
/// <param name="OutputIndex">The zero-based position of the fact in <c>FactsV1.Facts</c>.</param>
public sealed record FactDrop(string Reason, int OutputIndex);

/// <summary>The stable reasons a fact is dropped (02.5.3-T2 ordered rules 1–5).</summary>
public static class FactDropReasons
{
    /// <summary>Rule 1: no alias resolved (unknown, empty, or a partially-unknown fact with no known alias), F12.</summary>
    public const string UnknownAlias = "unknown_alias";

    /// <summary>Rule 2: the statement is empty, longer than 600 characters, or carries a lone UTF-16 surrogate.</summary>
    public const string StatementInvalid = "statement_invalid";

    /// <summary>Rule 2: the statement carries NUL, DEL or a C0 control character that is not whitespace.</summary>
    public const string ControlCharactersInvalid = "control_characters_invalid";

    /// <summary>Rule 3: an earlier fact has the same normalised statement and resolved source ids; first wins.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Rule 4: the statement is a verbatim copy of its cited sources, above the overlap threshold (F18).</summary>
    public const string VerbatimCopy = "verbatim_copy";

    /// <summary>Rule 5: the version already holds <c>MaxFacts</c> survivors, or the run reached its bounded scan window.</summary>
    public const string OverCap = "over_cap";
}
