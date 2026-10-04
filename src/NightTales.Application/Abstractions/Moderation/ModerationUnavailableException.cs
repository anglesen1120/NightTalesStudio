namespace NightTales.Application.Abstractions.Moderation;

/// <summary>
/// The moderation endpoint answered 4xx/5xx or timed out after the retry budget, or returned nothing usable
/// (02 README D12, 02.4.1-T1 failure handling). Fail closed: the job maps it to <c>story_generation_failed</c>,
/// never to a pass. The message carries the HTTP status only, never provider text (D19).
/// </summary>
public sealed class ModerationUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ModerationUnavailableException"/> class.</summary>
    /// <param name="status">The last HTTP status, or <see langword="null"/> for a timeout or transport failure.</param>
    public ModerationUnavailableException(int? status)
        : base(status is { } code
            ? $"The moderation endpoint failed with HTTP {code}."
            : "The moderation endpoint was unreachable or timed out.")
    {
        Status = status;
    }

    /// <summary>Gets the last HTTP status, or <see langword="null"/> for a timeout or transport failure.</summary>
    public int? Status { get; }
}
