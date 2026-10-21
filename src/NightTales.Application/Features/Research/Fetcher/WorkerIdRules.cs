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

    /// <summary>
    /// The same check as <see cref="MustBeWorkerId{T}"/> as a plain predicate, for callers (the internal listener's rate
    /// limiter) that must classify an id before the command's validator runs.
    /// </summary>
    /// <param name="workerId">The id from the request body.</param>
    /// <returns><see langword="true"/> when the id is 1 to <see cref="FetchResultLimits.MaxWorkerIdLength"/> allowed characters.</returns>
    public static bool IsValid(string? workerId) =>
        !string.IsNullOrEmpty(workerId)
        && workerId.Length <= FetchResultLimits.MaxWorkerIdLength
        && AllowedCharacters().IsMatch(workerId);

    [GeneratedRegex(@"\A[A-Za-z0-9._:-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedCharacters();
}
