using Microsoft.Extensions.Options;
using NightTales.Application.Rendering.Quality;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// The startup validation of <see cref="RenderQualityPolicy"/> (05 README D10, 05.5.1-T2 acceptance): a
/// loudness band whose ends are inverted, a share outside 0–1 or a non-positive drift bound is a
/// configuration error and must fail when the host starts, never when the first render is judged with a
/// policy that cannot mean anything.
/// </summary>
/// <remarks>
/// The validator sits here rather than next to the policy because it needs
/// <c>Microsoft.Extensions.Options</c>, and the Application layer's reference set is an allow-list
/// (09.1.2-T1) that keeps it at <c>ErrorOr</c> plus the wire contract and the abstractions.
/// </remarks>
public sealed class RenderQualityPolicyValidator : IValidateOptions<RenderQualityPolicy>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RenderQualityPolicy options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var prefix = $"{RenderQualityPolicy.SectionName}:";

        // The configuration binder accepts "NaN"/"Infinity"/"-Infinity" for a double key, and every
        // relational predicate below is false for NaN — so a non-finite value must be rejected by name
        // here, or it silently disables the check it configures (D1).
        RequireFinite(failures, prefix, nameof(options.MaxDurationDrift), options.MaxDurationDrift);
        RequireFinite(failures, prefix, nameof(options.MinLufs), options.MinLufs);
        RequireFinite(failures, prefix, nameof(options.MaxLufs), options.MaxLufs);
        RequireFinite(failures, prefix, nameof(options.MaxTruePeakDbtp), options.MaxTruePeakDbtp);
        RequireFinite(failures, prefix, nameof(options.MaxLongestSilenceSeconds), options.MaxLongestSilenceSeconds);
        RequireFinite(failures, prefix, nameof(options.MinSubtitleCoverage), options.MinSubtitleCoverage);
        RequireFinite(failures, prefix, nameof(options.SyncSuspectHintShare), options.SyncSuspectHintShare);

        if (options.PolicyVersion < 1)
        {
            failures.Add($"{prefix}{nameof(options.PolicyVersion)} must be at least 1.");
        }

        if (options.MaxDurationDrift is <= 0 or > 0.5)
        {
            failures.Add($"{prefix}{nameof(options.MaxDurationDrift)} must be greater than 0 and at most 0.5.");
        }

        if (options.MinLufs >= options.MaxLufs)
        {
            failures.Add($"{prefix}{nameof(options.MinLufs)} must be below {prefix}{nameof(options.MaxLufs)}.");
        }

        if (options.MinLufs >= 0 || options.MaxLufs > 0)
        {
            failures.Add($"{prefix}{nameof(options.MinLufs)} and {prefix}{nameof(options.MaxLufs)} must both be negative LUFS values.");
        }

        if (options.MaxTruePeakDbtp >= 0)
        {
            failures.Add($"{prefix}{nameof(options.MaxTruePeakDbtp)} must be a negative dBTP value.");
        }

        if (options.MaxLongestSilenceSeconds < 0)
        {
            failures.Add($"{prefix}{nameof(options.MaxLongestSilenceSeconds)} must not be negative.");
        }

        if (options.MinSubtitleCoverage is < 0 or > 1)
        {
            failures.Add($"{prefix}{nameof(options.MinSubtitleCoverage)} must be a share between 0 and 1.");
        }

        if (options.MinCues < 1)
        {
            failures.Add($"{prefix}{nameof(options.MinCues)} must be at least 1.");
        }

        if (options.SyncSuspectHintShare is < 0 or > 1)
        {
            failures.Add($"{prefix}{nameof(options.SyncSuspectHintShare)} must be a share between 0 and 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Appends a failure naming the key when the bound value is not a finite number.</summary>
    private static void RequireFinite(List<string> failures, string prefix, string key, double value)
    {
        if (!double.IsFinite(value))
        {
            failures.Add($"{prefix}{key} must be a finite number.");
        }
    }
}
