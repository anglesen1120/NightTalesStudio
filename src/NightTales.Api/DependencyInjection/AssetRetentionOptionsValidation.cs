using Microsoft.Extensions.Options;
using NightTales.Application.Features.VideoReview;

namespace NightTales.Api.DependencyInjection;

/// <summary>
/// Fails the Api's start-up on an unusable <c>Assets:RetentionDays</c> (09.7.1-T1, WT56): the window the
/// approve path re-stamps on an episode's live assets, with every problem listed.
/// </summary>
/// <remarks>
/// The checks live in Application (<see cref="AssetRetentionOptionsValidator.Validate"/>) because that assembly
/// cannot reference <c>Microsoft.Extensions.Options</c>; this is the <see cref="IValidateOptions{T}"/> adapter
/// of the Api's composition root, the <c>RenderIngestOptionsValidation</c> precedent of the Worker. The Worker
/// binds the same section for its ingest stamp (<c>Assets:UnapprovedRetentionDays</c>) with its own adapter.
/// </remarks>
internal sealed class AssetRetentionOptionsValidation : IValidateOptions<AssetRetentionOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AssetRetentionOptions options)
    {
        var problems = AssetRetentionOptionsValidator.Validate(options);

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems.Select(problem => $"{AssetRetentionOptions.SectionName}: {problem}"));
    }
}
