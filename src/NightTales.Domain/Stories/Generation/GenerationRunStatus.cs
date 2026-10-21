namespace NightTales.Domain.Stories.Generation;

/// <summary>Lifecycle of a <see cref="GenerationRun"/> (02 D2), stored as snake_case text.</summary>
public enum GenerationRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}
