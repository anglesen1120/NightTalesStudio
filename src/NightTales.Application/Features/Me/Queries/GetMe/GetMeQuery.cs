using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Me.Queries.GetMe;

/// <summary>
/// <c>GET /api/v1/me</c> (09.3.4-T1): the caller's own profile. The query has no id: it acts on
/// <c>ICurrentUser.Id</c> only, so no caller can address another account.
/// </summary>
public sealed record GetMeQuery : IQuery<ErrorOr<MeDto>>;
