using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Stories.Content;

namespace NightTales.Application.Features.Stories.Writing;

/// <summary>
/// The writer step of the story pipeline (02 README D21). It assembles, calls and meters; it does not
/// validate, retry or persist — <c>GenerateStoryJob</c> owns those.
/// </summary>
/// <remarks>
/// <c>ReviewAsync</c> and <c>ReviseAsync</c> (D21 rows 4–5) are added by 02.6.1 / 02.6.2 together with their
/// request and result types, which do not exist yet.
/// </remarks>
public interface IStoryWriter
{
    /// <summary>Writes one draft.</summary>
    /// <param name="req">The write request; <c>ViolatedRules</c> is set only on the single invalid-output retry.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The mapped outcome of the call; its usage has already been recorded.</returns>
    ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct);
}
