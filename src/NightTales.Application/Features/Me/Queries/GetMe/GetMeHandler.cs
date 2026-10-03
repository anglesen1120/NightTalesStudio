using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Me.Queries.GetMe;

/// <summary>
/// The one handler of <c>GET /me</c> (09.3.4-T1): reads the caller's row through <see cref="IUserProfileReader"/>
/// and adds the enabled allow-listed feature keys. A missing or deleted account is 401 (the token outlived it).
/// </summary>
/// <param name="currentUser">The authenticated caller; the only account this handler reads.</param>
/// <param name="reader">The no-tracking profile read.</param>
/// <param name="flags">The cached feature-flag read side.</param>
public sealed class GetMeHandler(ICurrentUser currentUser, IUserProfileReader reader, IFeatureFlags flags)
    : IQueryHandler<GetMeQuery, ErrorOr<MeDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<MeDto>> Handle(GetMeQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return MfaErrors.Unauthenticated();
        }

        var profile = await reader.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return MfaErrors.Unauthenticated();
        }

        var features = await MeFeatureAllowList.EnabledAsync(flags, cancellationToken).ConfigureAwait(false);

        return MeFeatureAllowList.ToDto(profile, features);
    }
}
