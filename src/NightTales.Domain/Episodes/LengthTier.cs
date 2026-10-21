namespace NightTales.Domain.Episodes;

/// <summary>Story length tier of an episode (00 overview §6 <c>episodes.length_tier</c>).</summary>
#pragma warning disable CA1720 // The member names are the frozen wire values (09 README D16), so `short`/`long` stay as written.
public enum LengthTier
{
    Short,
    Standard,
    Long,
}
#pragma warning restore CA1720
