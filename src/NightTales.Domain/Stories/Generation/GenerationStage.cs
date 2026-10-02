namespace NightTales.Domain.Stories.Generation;

/// <summary>
/// The stage a <see cref="GenerationRun"/> is in (02 D21), stored as snake_case text. The declaration order
/// is the forward order; <see cref="GenerationRun.AdvanceStage"/> allows one re-entry
/// (<see cref="Revising"/> → <see cref="Checking"/>).
/// </summary>
public enum GenerationStage
{
    Queued,
    Writing,
    Checking,
    Reviewing,
    Revising,
    Scoring,
    Saving,
    Done,
}
