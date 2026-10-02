using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Me.Commands.UpdateMe;

/// <summary>
/// <c>PATCH /api/v1/me</c> (09.3.4-T1): change the display name and/or the UI locale of the caller's own
/// account. The e-mail, role and MFA state are not writable here, and there is no user id: the handler
/// acts on <c>ICurrentUser.Id</c>.
/// </summary>
/// <remarks>
/// The reference shape of an audited command: <c>[Audited]</c> + <see cref="IAuditData"/> + a
/// <see cref="Summary"/> the handler fills, and no <c>SaveChanges</c> in the handler (09 README D5).
/// </remarks>
/// <param name="DisplayName">The new display name, or <see langword="null"/> to keep it.</param>
/// <param name="UiLocale">The new UI locale, or <see langword="null"/> to keep it.</param>
[Audited("account.profile_updated", Category.AccountSecurity, Visibility.User)]
public sealed record UpdateMeCommand(string? DisplayName, Language? UiLocale) : ICommand<ErrorOr<MeDto>>, IAuditData
{
    /// <summary>The delta the handler writes: field names and locales only, never the display-name text (09 README BD16).</summary>
    internal Dictionary<string, string> Summary { get; } = new(StringComparer.Ordinal);

    /// <summary>The projection of this command's audit row; the subject is the caller (filled by the behaviour).</summary>
    /// <returns>The audit projection.</returns>
    public AuditData ToAuditData() => new(SubjectUserId: null, EntityType: "user", EntityId: null, Summary);
}
