using ErrorOr;

namespace NightTales.Application.Abstractions.Stories;

/// <summary>Reads the persisted version selected by the caller's approved-story pointer.</summary>
public interface IStoryRenderInputProvider
{
    /// <summary>Returns the owner's render input for the exact supplied version, without generating text.</summary>
    /// <param name="storyVersionId">The approved version id resolved by the caller, never a current-version fallback.</param>
    /// <param name="ct">Cancels the database read.</param>
    /// <returns>The persisted input, or not-found when the version is unknown or belongs to another owner.</returns>
    ValueTask<ErrorOr<StoryRenderInput>> GetAsync(Guid storyVersionId, CancellationToken ct);
}
