using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Autopilot.Commands.AcceptAutopilotAuthorization;

/// <summary>
/// <c>POST /api/v1/series/{id}/autopilot/authorization</c> (02.8.3-T2 plan §3.7, task step 5): record the owner's
/// acceptance of the rendered authorization text, bound to the exact digest they read.
/// </summary>
/// <remarks>
/// <para>
/// <b>D-6: the pair, not the text.</b> The body carries <c>{ textVersion, textHash }</c>; the handler re-renders
/// both locales from the <em>stored</em> settings and accepts only an exact match, so a page rendered before a
/// settings change cannot be accepted (the 409 with <c>errors.textHash = stale_text</c> is that answer).
/// </para>
/// <para>
/// <b>The audit row's ids come from the handler.</b> The row id and the version only exist after the insert, so the
/// command carries them as <see cref="AcceptedAuthorizationId"/>/<see cref="AcceptedVersion"/> — the mutable
/// projection member <c>CreationAuditData</c> and 02.5.1-T1 established — and
/// <see cref="ToAuditData"/> answers <see langword="null"/> when the handler never set them.
/// </para>
/// </remarks>
/// <param name="Id">The series whose authorization is accepted.</param>
/// <param name="TextVersion">The template version the page displayed, e.g. <c>autopilot-auth.v1</c>.</param>
/// <param name="TextHash">The lowercase hex SHA-256 of the text the owner read and accepted.</param>
[Audited("autopilot.authorization_accepted", Category.Publishing, Visibility.User)]
public sealed record AcceptAutopilotAuthorizationCommand(Guid Id, string TextVersion, string TextHash)
    : ICommand<ErrorOr<Dtos.AcceptedAuthorizationDto>>, IAuditData
{
    /// <summary>The id of the row the handler created, or <see langword="null"/> before it succeeded.</summary>
    [JsonIgnore]
    public Guid? AcceptedAuthorizationId { get; set; }

    /// <summary>The version the handler stored, or <c>0</c> before it succeeded.</summary>
    [JsonIgnore]
    public int AcceptedVersion { get; set; }

    /// <inheritdoc />
    public AuditData? ToAuditData()
    {
        if (AcceptedAuthorizationId is not { } authorizationId)
        {
            return null;
        }

        return new AuditData(
            SubjectUserId: null,
            EntityType: AcceptAutopilotAuthorizationAudit.EntityType,
            EntityId: Id,
            Summary: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [AcceptAutopilotAuthorizationAudit.AuthorizationId] = authorizationId.ToString(),
                [AcceptAutopilotAuthorizationAudit.Version] = AcceptedVersion.ToString(CultureInfo.InvariantCulture),
                [AcceptAutopilotAuthorizationAudit.TextVersion] = TextVersion,
                [AcceptAutopilotAuthorizationAudit.TextHash] = TextHash,
            });
    }
}

/// <summary>
/// The <c>data</c> keys of <c>autopilot.authorization_accepted</c> (plan §4; D13: the version and the digest, never
/// the text).
/// </summary>
public static class AcceptAutopilotAuthorizationAudit
{
    /// <summary>The entity kind the row names.</summary>
    public const string EntityType = "series";

    /// <summary>The <c>data</c> member naming the created row.</summary>
    public const string AuthorizationId = "authorizationId";

    /// <summary>The <c>data</c> member carrying the 1-based version.</summary>
    public const string Version = "version";

    /// <summary>The <c>data</c> member carrying the template version.</summary>
    public const string TextVersion = "textVersion";

    /// <summary>The <c>data</c> member carrying the accepted digest.</summary>
    public const string TextHash = "textHash";
}
