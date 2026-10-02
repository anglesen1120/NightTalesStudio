namespace NightTales.Domain.Stories.Generation;

/// <summary>Who asked for a <see cref="GenerationRun"/> (02 D2), stored as snake_case text.</summary>
public enum GenerationTrigger
{
    User,
    Autopilot,
    System,
}
