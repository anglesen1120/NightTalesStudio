using FluentValidation;

namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// Shape validation of <c>POST /episodes/{id}/fact-sheet/generate</c> (02.5.3 plan §4.1): the
/// <c>Idempotency-Key</c> the route filter requires has the same 1–128 printable-ASCII contract as the
/// <c>idempotency_keys.key</c> column (09 CR-4), so a direct <c>ISender.Send</c> — a job or an autopilot
/// caller, which never passes the route filter — cannot store a key the column would refuse.
/// </summary>
/// <remarks>
/// The route's <c>RequireIdempotencyKey()</c> filter stays the caller-facing guard (400
/// <c>idempotency_key_required</c>, 09 README D6); this validator is its Application-side mirror and is what
/// the <c>ValidationBehavior</c> runs for every send.
/// </remarks>
public sealed class GenerateFactSheetCommandValidator : AbstractValidator<GenerateFactSheetCommand>
{
    /// <summary>The <c>idempotency_keys.key</c> maximum length.</summary>
    public const int MaxKeyLength = 128;

    private const char MinPrintableAscii = (char)0x21;

    private const char MaxPrintableAscii = (char)0x7E;

    public GenerateFactSheetCommandValidator()
    {
        RuleFor(command => command.IdempotencyKey)
            .Must(IsValidKey)
            .WithMessage("idempotency_key_required");
    }

    private static bool IsValidKey(string key)
    {
        if (key is null || key.Length is 0 or > MaxKeyLength)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (character < MinPrintableAscii || character > MaxPrintableAscii)
            {
                return false;
            }
        }

        return true;
    }
}
