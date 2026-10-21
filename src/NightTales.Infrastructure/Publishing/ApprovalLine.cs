using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The one sentence of <c>publication_consented</c> that depends on who consented (06.2.2-T2, 06 README D7/YR9),
/// built in code in both locales.
/// </summary>
/// <remarks>
/// <para>
/// The renderer substitutes flat <c>{{key}}</c> placeholders and has no conditionals (09-backend-api README §6),
/// so the choice cannot live in the template: it is made here and the already-rendered line travels as one value
/// of the <c>approvalLine</c> key.
/// </para>
/// <para>
/// The English wording is YR9's own; the Vietnamese is 06.2.2-T2's translation. A locale the enum does not
/// declare cannot occur — <see cref="Language"/> has two members (09 README D16) and the catalog serves both file
/// pairs — so an unknown value falls back to Vietnamese, the mapper's own rule (06.2.2-T2 §Failure handling).
/// </para>
/// </remarks>
public static class ApprovalLine
{
    /// <summary>The line of <c>publication_consented</c> for one consent source and locale.</summary>
    /// <param name="source">Who consented: the owner on the consent route, or the autopilot after the window.</param>
    /// <param name="locale">The render locale.</param>
    /// <returns>The sentence, already in the message's language.</returns>
    public static string For(ConsentSource source, Language locale) => (source, locale) switch
    {
        (ConsentSource.Autopilot, Language.En) => "Approved automatically after the veto window.",
        (ConsentSource.Autopilot, _) => "Đã tự động duyệt sau thời gian phủ quyết.",
        (_, Language.En) => "You approved this upload.",
        _ => "Bạn đã duyệt video này.",
    };
}
