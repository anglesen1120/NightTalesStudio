using ErrorOr;

namespace NightTales.Application.Common.Errors;

/// <summary>
/// The shared error factories, so a policy that appears in dozens of handlers is spelled once. The
/// HTTP status still comes from the code catalogue (<c>Api/Http/ErrorCatalogue.cs</c>), never from
/// <see cref="ErrorType"/> (09 README D7).
/// </summary>
public static class Errors
{
    /// <summary>
    /// The 404 of 09 README D10: a lookup through a tenant-filtered context found nothing, which means
    /// the row does not exist <em>or</em> belongs to another user. The caller must not be able to tell
    /// the two apart, so this is the answer for both — never 403 (id probing).
    /// </summary>
    public static Error NotFound() => Error.NotFound(code: ErrorCodes.NotFound);

    /// <summary>
    /// The 400 of 09 README D7 for a check only the handler can make (another row it just read, a
    /// catalog lookup, a cursor it just decoded): <c>validation_failed</c> with the camelCase
    /// <paramref name="field"/> the client must fix, which is the shape the <c>ValidationBehavior</c>
    /// produces for a FluentValidation failure.
    /// </summary>
    /// <param name="field">The camelCase wire field, for example <c>voiceId</c> or <c>limit</c>.</param>
    /// <param name="description">The client-safe reason.</param>
    public static Error ValidationFailed(string field, string description) =>
        Error.Validation(
            code: ErrorCodes.ValidationFailed,
            description: description,
            metadata: new Dictionary<string, object> { [ErrorMetadata.Field] = field });

    /// <summary>
    /// A 409 <c>keywords_flagged</c> (03 D10): the render or approve flow found a linter issue the creator must
    /// fix. The flags travel under <see cref="ErrorMetadata.Flags"/> and the writer renders them as the
    /// <c>errors[]</c> object array <c>{ segment, index, rule }</c> — the shape the web client's
    /// <c>keyword-flags.mapper</c> reads — never as the <c>field → messages</c> object of the other 400 codes.
    /// </summary>
    /// <param name="flags">The blocking issues, in linter order; capped at <see cref="MaxKeywordFlags"/>.</param>
    /// <remarks>
    /// The caller maps each issue through <c>RenderKeywordLinter.TryGetKeywordIndex</c> and never passes issue
    /// text, a keyword or a name: a flag is only a segment address, a chip index and a rule code (03 CR-18b).
    /// </remarks>
    public static Error KeywordsFlagged(IReadOnlyList<KeywordFlag> flags)
    {
        ArgumentNullException.ThrowIfNull(flags);

        var bounded = flags.Count <= MaxKeywordFlags
            ? flags
            : [.. flags.Take(MaxKeywordFlags)];

        return Error.Conflict(
            code: ErrorCodes.KeywordsFlagged,
            description: "Blocked keyword or name detected.",
            metadata: new Dictionary<string, object> { [ErrorMetadata.Flags] = bounded });
    }

    /// <summary>
    /// The 409 <c>keywords_flagged</c> of 03.2.2-T2 for an image plan with no renderable scene: the
    /// synthetic <c>{ segment: "meta", index: 0, rule: "no_scenes" }</c> row the web annotation merge
    /// places on the panel.
    /// </summary>
    public static Error NoScenes() =>
        KeywordsFlagged([new KeywordFlag(RenderErrorRules.MetaSegment, 0, RenderErrorRules.NoScenes)]);

    /// <summary>
    /// The most flag rows a <c>keywords_flagged</c> answer carries, the same bound the 02.6.3-T1 validator
    /// applies to its error list: a response stays small and the client's own 100-row cap is never overrun.
    /// </summary>
    public const int MaxKeywordFlags = 100;
}

/// <summary>
/// One blocking keyword-linter issue as the wire sees it (03 D10, 03 CR-18b): the segment address, the chip
/// index inside that segment and the failing rule code. It is the serialized form of the writer's
/// <c>errors[]</c> row for a <c>keywords_flagged</c> answer; no issue text, keyword or name is part of it.
/// </summary>
/// <param name="Segment">The segment address (<c>p:0</c>, <c>hook</c>, …), never raw text.</param>
/// <param name="Index">The keyword chip index inside the segment, from <c>RenderKeywordLinter.TryGetKeywordIndex</c>.</param>
/// <param name="Rule">The failing rule code, for example <c>keyword_invalid</c> or <c>no_scenes</c>.</param>
public sealed record KeywordFlag(string Segment, int Index, string Rule);

/// <summary>
/// The rule codes a <c>keywords_flagged</c> <see cref="KeywordFlag.Rule"/> can carry (03 D10, 03.2.2-T2).
/// The linter's own rule ids come from <c>RenderKeywordLinter</c>; <see cref="NoScenes"/> is the synthetic
/// rule of an image plan with no renderable scene.
/// </summary>
public static class RenderErrorRules
{
    /// <summary>The rule of an image plan with no renderable scene (03.2.2-T2).</summary>
    public const string NoScenes = "no_scenes";

    /// <summary>The synthetic segment the <see cref="NoScenes"/> row points at; the web panel places it there.</summary>
    public const string MetaSegment = "meta";
}
