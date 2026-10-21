namespace NightTales.Application.Common.Errors;

/// <summary>
/// The error-metadata keys shared by the producer side (the pipeline behaviors and the handlers) and
/// the consumer side (<c>NightTales.Api.Http.ProblemDetailsWriter</c>), so a key can never drift
/// between the two halves of the contract (09 README D7, 09.1.4-T1).
/// </summary>
/// <remarks>
/// <para>
/// These three keys are also the writer's allow-list (plan §5 [DECISION]): a metadata entry under one
/// of them is copied into the ProblemDetails body, and one under any other key stays inside the
/// process. A behavior therefore writes the constant, not a literal.
/// </para>
/// <para>
/// The keys are camelCase because they are also the wire names when they reach the body
/// (<c>{"code":"…","retryAfterSeconds":30}</c>).
/// </para>
/// </remarks>
public static class ErrorMetadata
{
    /// <summary>The camelCase field name of a validation failure; the writer groups <c>errors</c> by it.</summary>
    public const string Field = "field";

    /// <summary>The seconds a 429 client must wait; the writer mirrors it into the <c>Retry-After</c> header (02 CR-20).</summary>
    public const string RetryAfterSeconds = "retryAfterSeconds";

    /// <summary>The ceiling a request exceeded, so a client can see the limit it hit (09 README D14).</summary>
    public const string Limit = "limit";

    /// <summary>
    /// The structured keyword flags of a <c>keywords_flagged</c> answer (03 D10, 09.6.1-T1): an
    /// <see cref="System.Collections.Generic.IReadOnlyList{T}"/> of <see cref="KeywordFlag"/> the
    /// writer renders as the <c>errors[]</c> object array <c>{ segment, index, rule }</c>, instead of the
    /// <c>field → messages</c> object the other 400 codes carry. It is consumed, never copied as a
    /// top-level member, so the type behind it never has to be a bounded scalar.
    /// </summary>
    public const string Flags = "flags";
}
