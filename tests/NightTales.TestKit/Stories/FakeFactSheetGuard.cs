using ErrorOr;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Series;

namespace NightTales.TestKit.Stories;

/// <summary>
/// A scriptable <see cref="IFactSheetGuard"/> (02 CR-24): 02.5.5-T1 registers the real <c>FactSheetGuard</c>,
/// and a story test scripts this fake when it needs one exact sheet shape instead of the rows the real guard
/// reads. A true-crime episode is refused with <c>fact_sheet_required</c> unless <see cref="ApprovedSheet"/> is
/// set; any other topic gets <see cref="ApprovedFactSheet.None"/>.
/// </summary>
public sealed class FakeFactSheetGuard : IFactSheetGuard
{
    /// <summary>The sheet returned to a true-crime episode; null makes the guard refuse it.</summary>
    public ApprovedFactSheet? ApprovedSheet { get; set; }

    /// <inheritdoc />
    public ValueTask<ErrorOr<ApprovedFactSheet>> EnsureApprovedAsync(Guid episodeId, Topic topic, CancellationToken ct)
    {
        if (topic.IsFiction())
        {
            return ValueTask.FromResult<ErrorOr<ApprovedFactSheet>>(ApprovedFactSheet.None);
        }

        return ValueTask.FromResult<ErrorOr<ApprovedFactSheet>>(
            ApprovedSheet is { } sheet
                ? sheet
                : Error.Conflict(ErrorCodes.FactSheetRequired, "A true-crime story needs an approved fact sheet."));
    }
}
