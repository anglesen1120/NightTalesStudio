namespace NightTales.Domain.Stories.Review;

/// <summary>A place where the draft presents something as a real event, person or organisation (02 README F31; feeds 02.8.2-T2).</summary>
/// <param name="Segment">The D22 address of the segment.</param>
/// <param name="Evidence">A quote of the draft, at most 200 characters.</param>
public sealed record RealWorldClaim(string Segment, string Evidence);
