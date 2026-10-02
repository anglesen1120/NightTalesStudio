using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.Features.Platform.Flags.Queries;

/// <summary>
/// <c>nts-worker flags list</c> (09 README BD13, 09.9.2-T2 §Operator CLI): every flag row the platform
/// seeds, so an operator can see what is currently on before changing it.
/// </summary>
/// <remarks>
/// Like <see cref="SetFeatureFlagCommand"/> this is a CLI-only message: no HTTP route dispatches it, so
/// the D4 marker is <see cref="AllowAnonymousRequestAttribute"/> and the operator boundary is the Worker
/// host. The answer carries no user data — a flag key and a boolean are all the table has.
/// </remarks>
[AllowAnonymousRequest]
public sealed record ListFeatureFlagsQuery : IQuery<ErrorOr<IReadOnlyList<FeatureFlagListItemDto>>>;

/// <summary>One row of the operator listing (09.9.2-T2 §Operator CLI).</summary>
/// <param name="Key">The flag key.</param>
/// <param name="Enabled">Whether the feature is on.</param>
/// <remarks>
/// The <c>rules</c> document is deliberately absent: the P reader answers from <c>enabled</c> only and
/// never reads it (BD13/WT13), so the CLI prints it as the fixed marker <c>(unused in P)</c> instead of
/// shaping a contract P does not honour.
/// </remarks>
public sealed record FeatureFlagListItemDto(string Key, bool Enabled);
