using System.Text.RegularExpressions;
using FluentValidation;

namespace NightTales.Application.Features.Research.Fetcher;

/// <summary>The one rule for the <c>workerId</c> every internal fetcher route carries (02.5.2-T2).</summary>
public static partial class WorkerIdRules
{
    /// <summary>
    /// Requires 1 to <see cref="FetchResultLimits.MaxWorkerIdLength"/> characters of ASCII letters, digits, <c>.</c>,
    /// <c>_</c>, <c>:</c> and <c>-</c>: the id is stored in <c>fetch_tasks.leased_by</c> and is a rate-limit partition
    /// key, so a control character, a surrogate or a megabyte of text must not reach either.
    /// </summary>
    /// <typeparam name="T">The command type.</typeparam>
    /// <param name="rule">The rule builder of the <c>WorkerId</c> member.</param>
    public static IRuleBuilderOptions<T, string?> MustBeWorkerId<T>(this IRuleBuilder<T, string?> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule
            .NotEmpty()
            .MaximumLength(FetchResultLimits.MaxWorkerIdLength)
            .Must(value => value is null || AllowedCharacters().IsMatch(value))
            .WithMessage("workerId may only contain letters, digits, '.', '_', ':' and '-'.");
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedCharacters();
}
