namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// What one spent action token authorized (09 README BD3/WF11, 06 README YR8): the user the token was issued
/// to and, for a subject-scoped flow, the row it is about.
/// </summary>
/// <remarks>
/// <para>
/// The subject never travels inside the token itself (BD3 hashes 32 random bytes and stores nothing else), so
/// the consume statement returns it from the row beside the owner. A caller of a subject-scoped flow MUST
/// compare <see cref="SubjectId"/> with the row its request names: a token minted for one publication is
/// therefore unusable against another, and the mismatch is answered with the flow's own generic refusal
/// (06.2.2-T2's <c>cancel_token_invalid</c>) rather than a cross-object write.
/// </para>
/// <para>
/// A plain flow (password reset, e-mail verification) keeps <see cref="SubjectId"/> <see langword="null"/>,
/// exactly as the row's nullable <c>subject_id</c> column spells it.
/// </para>
/// </remarks>
/// <param name="UserId">The owner of the token — the only identity an anonymous request may act as.</param>
/// <param name="SubjectId">
/// The row the token is about for a subject-scoped flow (06 CR-6/YR8), or <see langword="null"/> for a plain
/// one.
/// </param>
public sealed record ConsumedActionToken(Guid UserId, Guid? SubjectId);
