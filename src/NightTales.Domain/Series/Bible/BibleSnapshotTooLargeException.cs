namespace NightTales.Domain.Series.Bible;

/// <summary>
/// Raised when a series bible still exceeds the writer budget after every truncation step (02.6.3 plan §4 U4).
/// The message carries sizes only: bible text never appears in an exception or a log (D19).
/// </summary>
public sealed class BibleSnapshotTooLargeException : Exception
{
    /// <summary>Creates the exception for a bible that could not be brought under the budget.</summary>
    /// <param name="measuredBytes">The measured size, in UTF-8 bytes, after the last truncation step.</param>
    /// <param name="maxChars">The budget the bible had to fit (<c>Bible:Prompt:MaxChars</c>).</param>
    public BibleSnapshotTooLargeException(int measuredBytes, int maxChars)
        : base($"The series bible is {measuredBytes} bytes after every truncation step; the writer budget is {maxChars} bytes.")
    {
        MeasuredBytes = measuredBytes;
        MaxChars = maxChars;
    }

    /// <summary>The measured size, in UTF-8 bytes, after the last truncation step.</summary>
    public int MeasuredBytes { get; }

    /// <summary>The budget the bible had to fit.</summary>
    public int MaxChars { get; }
}
