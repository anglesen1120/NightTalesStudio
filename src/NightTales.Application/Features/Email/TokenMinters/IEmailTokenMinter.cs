using ErrorOr;

namespace NightTales.Application.Features.Email.TokenMinters;

/// <summary>
/// The send-time producer of one token-bearing template's values (09 README §B10 WT17, WT30, 09.2.6-T3):
/// the reset link of <c>password_reset</c>, later the verify link of <c>email_verify</c>.
/// </summary>
/// <remarks>
/// <para>
/// It runs inside <c>SendEmailJob</c> and nowhere else. The row it creates — a single-use
/// <c>user_action_tokens</c> row — and the link it returns exist only for the duration of the send: the
/// link goes into the rendered body, never into <c>email_messages.params</c>, never into the outbox payload
/// and never into a log (09 README BD16). That is why the queueing side carries the user id and the
/// token-free values only, and why a resend invalidates the previous link through the token service rather
/// than through an edit of the stored message.
/// </para>
/// <para>
/// One implementation exists per token-bearing template, registered as a keyed service by its template
/// name, so a template with no minter is a template nothing mints for.
/// </para>
/// </remarks>
public interface IEmailTokenMinter
{
    /// <summary>Mints the values this template needs at send time.</summary>
    /// <param name="userId">The owner the token authorizes.</param>
    /// <param name="templateName">The template being sent; a minter refuses a template it does not own.</param>
    /// <param name="parameters">The producer's queued values, for a minter that has to compose with them.</param>
    /// <param name="cancellationToken">Cancels the token write.</param>
    /// <returns>The values to add to the render dictionary, or an error that fails the attempt.</returns>
    Task<ErrorOr<IReadOnlyDictionary<string, string>>> MintAsync(
        Guid userId,
        string templateName,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken);
}
