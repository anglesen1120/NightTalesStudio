using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Me.Commands.UpdateMe;

/// <summary>
/// The one handler of <c>PATCH /me</c> (09.3.4-T1): applies the non-null members to the caller's tracked
/// account and records the delta in the command's audit summary. It never calls <c>SaveChangesAsync</c> or
/// opens a transaction: <c>UnitOfWorkBehavior</c> commits the change and the audit row together (09 README D5).
/// </summary>
/// <param name="currentUser">The authenticated caller; the only account this handler writes.</param>
/// <param name="writer">The tracked profile write.</param>
/// <param name="flags">The cached feature-flag read side, for the same <c>features</c> field as <c>GET /me</c>.</param>
public sealed class UpdateMeHandler(ICurrentUser currentUser, IUserProfileWriter writer, IFeatureFlags flags)
    : ICommandHandler<UpdateMeCommand, ErrorOr<MeDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<MeDto>> Handle(UpdateMeCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return MfaErrors.Unauthenticated();
        }

        var update = await writer
            .UpdateAsync(userId, command.DisplayName, command.UiLocale, cancellationToken)
            .ConfigureAwait(false);

        if (update is null)
        {
            // The token outlived the account (missing or deleted): nothing was written.
            return MfaErrors.Unauthenticated();
        }

        // The documented audit shape (09.3.4 plan §5): {"fields":[...],"uiLocale":{"from","to"}}. A name change
        // records only the field name, never the text (BD16). Nothing changed → empty Summary → no audit row.
        var fields = new List<string>(2);

        if (update.DisplayNameChanged)
        {
            fields.Add("displayName");
        }

        if (command.UiLocale is { } locale && locale != update.PreviousUiLocale)
        {
            fields.Add("uiLocale");
            command.Summary["uiLocale"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["from"] = Wire(update.PreviousUiLocale),
                ["to"] = Wire(locale),
            };
        }

        if (fields.Count > 0)
        {
            command.Summary["fields"] = fields;
        }

        var features = await MeFeatureAllowList.EnabledAsync(flags, cancellationToken).ConfigureAwait(false);

        return MeFeatureAllowList.ToDto(update.Profile, features);
    }

    private static string Wire(NightTales.Domain.Common.Language language) => language.ToString().ToLowerInvariant();
}
