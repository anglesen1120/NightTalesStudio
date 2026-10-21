using Microsoft.Extensions.Options;
using NightTales.Application.Rendering;

namespace NightTales.Worker.DependencyInjection;

/// <summary>Fails the start-up on an invalid <c>Render:Ingest</c> or <c>Assets</c> section (09.6.3-T2), with every
/// problem listed: the ingest's temp directory must be creatable and writable, and the retention window must be a
/// sane number of days.</summary>
/// <remarks>
/// The checks live in Application (<see cref="RenderIngestOptionsValidator"/>, the <c>RenderOptionsValidator</c>
/// and <c>ImagesOptionsValidator</c> precedent) because that assembly cannot reference
/// <c>Microsoft.Extensions.Options</c>; this is the <see cref="IValidateOptions{T}"/> adapter of the Worker's
/// composition root.
/// </remarks>
internal sealed class RenderIngestOptionsValidation : IValidateOptions<RenderIngestOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RenderIngestOptions options)
    {
        var problems = RenderIngestOptionsValidator.Validate(options);

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems.Select(problem => $"{RenderIngestOptions.SectionName}: {problem}"));
    }
}
