using ErrorOr;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Stories.Generation;

/// <summary>
/// The default <see cref="IFactSheetGuard"/> until 02.5.5-T1 registers the real one (02 CR-24): no fact sheet
/// exists yet, so a fiction episode needs none and a true-crime episode fails closed with
/// <c>fact_sheet_required</c>. Registered with <c>TryAdd</c>, so the real guard replaces it additively.
/// </summary>
internal sealed class NullFactSheetGuard : IFactSheetGuard
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<ApprovedFactSheet>> EnsureApprovedAsync(Guid episodeId, Topic topic, CancellationToken ct) =>
        ValueTask.FromResult<ErrorOr<ApprovedFactSheet>>(
            topic.IsFiction()
                ? ApprovedFactSheet.None
                : Error.Conflict(ErrorCodes.FactSheetRequired, "A true-crime story needs an approved fact sheet."));
}
