namespace NightTales.Domain.Stories.Content;

/// <summary>
/// The D3 derived fields, recomputed by the domain on every save: <c>body</c>, <c>keywords</c> and
/// <c>word_count</c>. The 03.2.1 keyword cap <c>ceil(D × K)</c> is deliberately not enforced here — it is
/// a checker/hint concern (F09) — but the 02.3.4 checker reuses <see cref="StoryText.Tokenize"/> for it.
/// </summary>
/// <param name="Body">The hook, the paragraph texts and the ending joined with a blank line.</param>
/// <param name="Keywords">Hook ++ paragraphs ++ ending keywords, verbatim in order (D3 does not dedupe).</param>
/// <param name="WordCount">The D4 token count of <see cref="Body"/>.</param>
public sealed record DerivedStoryFields(string Body, IReadOnlyList<string> Keywords, int WordCount)
{
    /// <summary>Derives the three fields of one draft (02 README D3, D21 step 2).</summary>
    /// <param name="content">The draft as the writer returned it; the draft is not modified.</param>
    /// <returns>The derived body, keywords and word count.</returns>
    public static DerivedStoryFields From(StoryContentV1 content)
    {
        ArgumentNullException.ThrowIfNull(content);

        // A deserialised draft can carry a null member even though the record declares it non-nullable;
        // the validator reports that (never throws), so deriving tolerates it exactly as OrEmpty does
        // elsewhere: a missing hook/ending contributes no text and a null paragraph element is skipped.
        var paragraphs = content.Paragraphs ?? [];
        var joined = string.Join("\n\n", paragraphs.Select(paragraph => paragraph?.Text ?? string.Empty));

        // D3: hook + blank line + the paragraph texts + blank line + ending. Two 3-argument Concat calls
        // keep the formula exact without the params-array allocation of a 5-argument call.
        var body = string.Concat(
            string.Concat(content.Hook?.Text ?? string.Empty, "\n\n", joined),
            "\n\n",
            content.Ending?.Text ?? string.Empty);

        var keywords = new List<string>();
        Append(content.Hook, keywords);
        foreach (var paragraph in paragraphs)
        {
            Append(paragraph, keywords);
        }

        Append(content.Ending, keywords);

        return new DerivedStoryFields(body, keywords, StoryText.Tokenize(body).Count);
    }

    /// <summary>Appends one segment's keywords in D3 order; a missing segment or list contributes nothing.</summary>
    private static void Append(StorySegmentV1? segment, List<string> keywords)
    {
        if (segment?.Keywords is null)
        {
            return;
        }

        foreach (var keyword in segment.Keywords)
        {
            keywords.Add(keyword);
        }
    }
}
