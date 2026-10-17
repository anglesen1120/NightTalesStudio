using FluentValidation;

namespace NightTales.Application.Features.Research.Commands.FindSources;

/// <summary>
/// Shape validation of <c>POST /episodes/{id}/sources/find</c> (02.7 plan §4 U1): the route value, the typed
/// query's 300-character bound after trimming and control-character stripping, and the <c>Idempotency-Key</c>
/// contract of the <c>idempotency_keys.key</c> column (09 CR-4, as in <c>GenerateFactSheetCommandValidator</c>).
/// </summary>
/// <remarks>
/// The route's <c>RequireIdempotencyKey()</c> filter stays the caller-facing guard (400
/// <c>idempotency_key_required</c>, 09 README D6); this validator is its Application-side mirror and is what the
/// <c>ValidationBehavior</c> runs for every send, including a job or an autopilot caller that never passes the
/// route filter. Every other gate (flag, allow-list, state, active run, budget) belongs to the ordered handler
/// checks, because each needs the database.
/// </remarks>
public sealed class FindSourcesCommandValidator : AbstractValidator<FindSourcesCommand>
{
    /// <summary>The <c>idempotency_keys.key</c> maximum length.</summary>
    public const int MaxKeyLength = 128;

    private const char MinPrintableAscii = (char)0x21;

    private const char MaxPrintableAscii = (char)0x7E;

    public FindSourcesCommandValidator()
    {
        RuleFor(command => command.EpisodeId).NotEmpty();

        // The bound applies to the stored value, not the raw one: leading control characters and padding do not
        // push an otherwise acceptable query over the limit.
        RuleFor(command => command.Query)
            .Must(query => FindSourcesCommand.NormalizeQuery(query) is not { Length: > FindSourcesCommand.MaxQueryLength })
            .WithMessage("query_too_long");

        RuleFor(command => command.IdempotencyKey)
            .Must(IsValidKey)
            .WithMessage("idempotency_key_required");
    }

    private static bool IsValidKey(string? key)
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
