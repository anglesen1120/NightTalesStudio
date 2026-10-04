using NightTales.Domain.Compliance;

namespace NightTales.Application.Abstractions.Moderation;

/// <summary>
/// The only writer of <c>moderation_events</c> (02 README D12). The row is evidence, not control flow: a database
/// failure is logged and swallowed, so the run outcome never changes because the row could not be written.
/// </summary>
public interface IModerationEventWriter
{
    /// <summary>Writes one row in its own scope and transaction, independent of the job's.</summary>
    /// <param name="userId">The owner of the run.</param>
    /// <param name="episodeId">The episode, or <see langword="null"/> for a series-level run.</param>
    /// <param name="source">The component that raised the event; <see cref="ModerationSource.OpenaiText"/> for text.</param>
    /// <param name="stage">The stage; written as its snake_case name.</param>
    /// <param name="categories">The flagged categories; empty when a refusal named none.</param>
    /// <param name="ownText">
    /// The segment of <b>our own</b> brief or draft the event is about (D19/D22): never a source text or a stored
    /// version. The row keeps its first 300 characters.
    /// </param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>A task that completes when the row is saved or the failure is logged.</returns>
    Task WriteAsync(
        Guid userId,
        Guid? episodeId,
        ModerationSource source,
        ModerationStage stage,
        IReadOnlyCollection<string> categories,
        string? ownText,
        CancellationToken ct);
}
