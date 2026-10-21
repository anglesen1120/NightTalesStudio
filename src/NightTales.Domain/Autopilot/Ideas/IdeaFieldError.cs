namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// One content violation of an episode idea (02.8.1 plan §3.2): the JSON path of the offending field, the
/// closed code 02.8.1-T2 turns into the <c>invalid</c> rejection and an optional, content-free detail.
/// </summary>
/// <remarks>
/// The detail is a limit, a vocabulary name or the offending marker — never the candidate's text, which is
/// model output and never reaches a log (D19).
/// </remarks>
/// <param name="Field">The JSON path, for example <c>title</c>, <c>beats[2]</c> or <c>structure.sub_genre</c>.</param>
/// <param name="Code">One of the code constants of <see cref="IdeaValidator"/>.</param>
/// <param name="Detail">A limit, a vocabulary value or a marker name; never content.</param>
public sealed record IdeaFieldError(string Field, string Code, string? Detail);
