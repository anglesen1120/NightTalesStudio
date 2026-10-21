using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Features.Email;
using NightTales.Application.Features.Email.TokenMinters;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// Resolves and runs the keyed <see cref="IEmailTokenMinter"/> of one template (09 README §B10 WT17,
/// 09.2.6-T3).
/// </summary>
/// <remarks>
/// <para>
/// The catalog decides first: a template whose entry declares no minter key mints nothing and answers an
/// empty set without touching the container. Only then is the template's keyed minter resolved, which is
/// what keeps the token-bearing templates' dependencies out of every other e-mail's graph: a build whose
/// <c>password_reset</c> minter needs a service the host does not have yet fails when such a message is
/// sent, not when any message is.
/// </para>
/// <para>
/// A template that declares minter keys and has no registered minter is a build error, and it throws as
/// one — the S-phase <c>email_verify</c> minter arrives with 09.3.1-T3, which is what registers it.
/// </para>
/// </remarks>
/// <param name="services">The scope the job runs in; the keyed minters are scoped beside it.</param>
internal sealed class EmailTokenMinters(IServiceProvider services) : IEmailTokenMinters
{
    /// <inheritdoc />
    public async Task<ErrorOr<IReadOnlyDictionary<string, string>>> MintAsync(
        Guid userId,
        string templateName,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        ArgumentNullException.ThrowIfNull(parameters);

        var definition = EmailTemplateCatalog.Find(templateName);
        if (definition is null || definition.MinterKeys.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var minter = services.GetKeyedService<IEmailTokenMinter>(templateName)
            ?? throw new InvalidOperationException(
                $"No IEmailTokenMinter is registered for the '{templateName}' template, whose catalog entry declares the send-time key(s) "
                + $"[{string.Join(", ", definition.MinterKeys.Order(StringComparer.Ordinal))}] (09 README §B10 WT17).");

        return await minter
            .MintAsync(userId, templateName, parameters, cancellationToken)
            .ConfigureAwait(false);
    }
}
