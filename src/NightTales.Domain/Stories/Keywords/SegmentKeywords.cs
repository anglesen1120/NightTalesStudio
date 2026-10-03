namespace NightTales.Domain.Stories.Keywords;

/// <summary>
/// The render keywords of one story segment, in story order (03.2 plan §3.1). <see cref="Segment"/> uses the
/// 02 D22 address (<c>hook</c>, <c>p:&lt;i&gt;</c>, <c>ending</c>); the first keyword is the segment's primary
/// scene, the optional second one a spare that the image plan drops first when the cap bites.
/// </summary>
/// <param name="Segment">The segment address.</param>
/// <param name="Keywords">The segment's keywords, primary first.</param>
public sealed record SegmentKeywords(string Segment, IReadOnlyList<string> Keywords);
