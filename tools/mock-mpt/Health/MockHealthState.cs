namespace NightTales.MockMpt.Health;

/// <summary>
/// The live values the mock's health source publishes: how many tasks are non-terminal right now. The fake
/// backend writes it after every state change; the health source only reads it, so a health pull never touches
/// the store, MPT, the disk or the checks (11 README D19 Q1).
/// </summary>
public sealed class MockHealthState
{
    private int _activeTasks;

    /// <summary>The non-terminal task count.</summary>
    public int ActiveTasks => Volatile.Read(ref _activeTasks);

    /// <summary>Replaces the published count (never negative).</summary>
    public void SetActiveTasks(int value) => Volatile.Write(ref _activeTasks, Math.Max(0, value));
}
