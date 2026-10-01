using ErrorOr;

namespace NightTales.Application.Features.Email.TokenMinters;

/// <summary>
/// Resolves and runs the <see cref="IEmailTokenMinter"/> of one template (09 README §B10 WT17,
/// 09.2.6-T3): the single dependency <c>SendEmailJob</c> takes, so the job never knows which templates
/// mint a token.
/// </summary>
/// <remarks>
/// <para>
/// It answers an empty dictionary for a template whose catalog entry declares no minter key, and it fails
/// when a template does declare one and no minter is registered — the S-phase <c>email_verify</c> link
/// arrives with 09.3.1-T3's minter. Resolution is by template name (a keyed registration), which is what
/// keeps a template's minter — and its dependencies — out of the graph of every other e-mail: a
/// token-bearing template whose minter is not registered yet cannot break the templates that never mint.
/// </para>
/// </remarks>
public interface IEmailTokenMinters
{
    /// <summary>Produces the send-time values of <paramref name="templateName"/>.</summary>
    /// <param name="userId">The owner the message is sent to.</param>
    /// <param name="templateName">The template being sent.</param>
    /// <param name="parameters">The producer's queued values.</param>
    /// <param name="cancellationToken">Cancels the mint.</param>
    /// <returns>The values to merge into the render dictionary; empty when the template mints nothing.</returns>
    Task<ErrorOr<IReadOnlyDictionary<string, string>>> MintAsync(
        Guid userId,
        string templateName,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken);
}
