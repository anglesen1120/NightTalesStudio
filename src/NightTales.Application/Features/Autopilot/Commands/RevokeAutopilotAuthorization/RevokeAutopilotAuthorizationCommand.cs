using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Autopilot.Commands.RevokeAutopilotAuthorization;

/// <summary>
/// <c>DELETE /api/v1/series/{id}/autopilot/authorization</c> (02.8.3-T2 plan §3.7, task step 5): the owner takes their
/// consent back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent, and that is the contract.</b> A series with no live authorization answers success and writes no
/// audit row (the projection answers <see langword="null"/>), so a double click, or a delete after an automatic
/// revocation, is not an error. The route answers 204 either way.
/// </para>
/// <para>
/// <b>Autopilot stays enabled.</b> Plan §3.7 is explicit: revoking does not switch the autopilot off — its runs simply
/// wait for a new acceptance, which is what stops a revocation from silently changing a setting the owner did not
/// touch.
/// </para>
/// </remarks>
/// <param name="Id">The series whose authorization is revoked.</param>
[Audited("autopilot.authorization_revoked", Category.Publishing, Visibility.User)]
public sealed record RevokeAutopilotAuthorizationCommand(Guid Id)
    : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <summary>The id of the revoked row, or <see langword="null"/> when there was nothing to revoke.</summary>
    [JsonIgnore]
    public Guid? RevokedAuthorizationId { get; set; }

    /// <summary>The version of the revoked row, or <c>0</c>.</summary>
    [JsonIgnore]
    public int RevokedVersion { get; set; }

    /// <inheritdoc />
    public AuditData? ToAuditData()
    {
        if (RevokedAuthorizationId is not { } authorizationId)
        {
            return null;
        }

        return new AuditData(
            SubjectUserId: null,
            EntityType: "series",
            EntityId: Id,
            Summary: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["authorizationId"] = authorizationId.ToString(),
                ["version"] = RevokedVersion.ToString(CultureInfo.InvariantCulture),
                ["reason"] = "owner_revoked",
            });
    }
}
