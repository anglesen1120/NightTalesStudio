using System.Globalization;
using NightTales.Domain.Stories.Content;

namespace NightTales.Domain.Stories.Similarity;

/// <summary>A story's separately scored segments; windows never cross their boundaries.</summary>
public sealed record StoryText(IReadOnlyList<StorySegment> Segments)
{
    /// <summary>Projects title, hook, numbered paragraphs and ending, excluding all metadata.</summary>
    public static StoryText From(StoryContentV1 c)
    {
        var segments = new List<StorySegment>(c.Paragraphs.Count + 3)
        {
            new("title", c.Title),
            new("hook", c.Hook.Text),
        };
        for (var i = 0; i < c.Paragraphs.Count; i++)
        {
            segments.Add(new StorySegment("p:" + i.ToString(CultureInfo.InvariantCulture), c.Paragraphs[i].Text));
        }

        segments.Add(new StorySegment("ending", c.Ending.Text));
        return new StoryText(segments);
    }
}
