namespace NightTales.Application.UnitTests.Pipeline.Probes;

/// <summary>
/// Records the order in which pipeline participants ran, per test host instance (never static, so
/// parallel test classes cannot observe each other).
/// </summary>
public sealed class PipelineTrace
{
    private readonly List<string> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Record(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }
}
