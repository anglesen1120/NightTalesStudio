using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Publishing.Channels.Queries.ListChannels;

/// <summary>
/// Reads the caller's own channel connections for <c>GET /channels</c> (06.1.2-T2 task §Design, 06 README D4/D6).
/// </summary>
/// <remarks>
/// It carries nothing: the tenant filter of 09 README D10 scopes the read to the calling account, so there is no
/// owner, page or filter parameter to pass — one user has a handful of connections and the endpoint answers them
/// all. It answers <see cref="ErrorOr{T}"/> like every other query in this repo (the Mediator behaviours are
/// constrained to <c>TResponse : IErrorOr</c>), even though the list itself has no failure mode.
/// </remarks>
public sealed record ListChannelsQuery : IQuery<ErrorOr<IReadOnlyList<ChannelConnectionDto>>>;
