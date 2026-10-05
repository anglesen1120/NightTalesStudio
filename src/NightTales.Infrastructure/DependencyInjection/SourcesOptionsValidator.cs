using Microsoft.Extensions.Options;
using NightTales.Application.Features.Research.Sources;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>Fails the start-up on a cap of 0 or a <c>MaxNoteChars</c> outside [1, 10,000] (02.5.1 T1 Config).</summary>
public sealed class SourcesOptionsValidator : IValidateOptions<SourcesOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SourcesOptions options)
    {
        var problems = SourcesOptions.Validate(options);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
