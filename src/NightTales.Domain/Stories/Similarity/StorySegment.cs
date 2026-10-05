namespace NightTales.Domain.Stories.Similarity;

/// <summary>A scored address and its prose; metadata is not a segment.</summary>
public sealed record StorySegment(string Address, string Text);
