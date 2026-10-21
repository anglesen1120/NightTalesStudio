using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// One e-mail a feature asks the platform to send (09 README BD9, 09.2.6-T3): the argument of
/// <see cref="IEmailSender.QueueAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The producer passes ids, a catalog template name, the render locale and the template's allow-listed
/// values — nothing else. It never passes an address (the job reads the current one from
/// <c>users.email</c>) and it never passes a token: a token-bearing value such as the reset
/// <c>link</c> is minted inside <c>SendEmailJob</c>, so it never reaches this record, the outbox payload
/// or <c>email_messages.params</c> (09 README §B10 WT17, WT30).
/// </para>
/// <para>
/// All producers are command handlers or outbox handlers, so <see cref="IEmailSender.QueueAsync"/> always
/// joins an existing unit of work and its row and the <c>email_requested.v1</c> outbox row commit
/// together (09 README D5/D11).
/// </para>
/// </remarks>
/// <param name="UserId">The recipient; a row of the same id must exist and be active.</param>
/// <param name="Template">A key of <c>EmailTemplateCatalog</c>; an unknown name is <c>email_template_unknown</c>.</param>
/// <param name="Locale">
/// The render locale — the owner's <c>ui_locale</c>, as <see cref="Language"/> (09 README D16: one enum
/// for both, never a second one).
/// </param>
/// <param name="Params">
/// The render values the template's producer allow-list accepts. A key outside it is
/// <c>email_template_keys</c>, which is also what a token-bearing key (<c>link</c>) answers.
/// </param>
public sealed record EmailMessage(
    Guid UserId,
    string Template,
    Language Locale,
    IReadOnlyDictionary<string, string> Params);
