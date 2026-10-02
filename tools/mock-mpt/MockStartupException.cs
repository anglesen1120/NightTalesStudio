namespace NightTales.MockMpt;

/// <summary>
/// A start-up failure of the mock that must reach the developer as a named message and exit code 2
/// (11.1.2-T1 Failure handling): a bad <c>MOCK_*</c> value, a missing or empty fixtures directory, or
/// missing canned media with no <c>ffmpeg</c> to generate it. It never carries a submitted body or a key.
/// </summary>
public sealed class MockStartupException : Exception
{
    public MockStartupException(string message)
        : base(message)
    {
    }

    public MockStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
