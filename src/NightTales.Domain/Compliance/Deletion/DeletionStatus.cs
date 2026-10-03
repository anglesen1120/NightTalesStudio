namespace NightTales.Domain.Compliance.Deletion;

/// <summary>Lifecycle of a <see cref="DeletionRequest"/>.</summary>
public enum DeletionStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
}
