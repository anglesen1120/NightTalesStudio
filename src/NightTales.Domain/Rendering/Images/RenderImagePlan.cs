namespace NightTales.Domain.Rendering.Images;

/// <summary>One keyword the render will turn into one image: its slot, its segment address and the keyword itself.</summary>
/// <param name="Slot">0-based position in the sent <c>video_terms</c>.</param>
/// <param name="Segment">The 02 D22 address of the owning segment.</param>
/// <param name="Keyword">The keyword, byte-identical to the story's.</param>
public sealed record PlannedTerm(int Slot, string Segment, string Keyword);

/// <summary>
/// The images of one render (03 README D3): the capped, ordered terms plus the counts the budget guard and the
/// outcome classifier read. Equality is by value, element by element, so two plans of the same input are equal.
/// </summary>
public sealed record RenderImagePlan
{
    /// <summary>Creates a plan.</summary>
    /// <param name="terms">The planned terms in story order.</param>
    /// <param name="clipSeconds">Seconds per image.</param>
    /// <param name="neededImages">The images the narration needs, <c>ceil(seconds ÷ clip)</c>.</param>
    /// <param name="maxTerms">The term cap: needed plus spares, never above the tier maximum.</param>
    /// <param name="trimmedCount">How many keywords the cap removed.</param>
    /// <param name="hintCodes">The advisory hint codes.</param>
    public RenderImagePlan(
        IReadOnlyList<PlannedTerm> terms,
        int clipSeconds,
        int neededImages,
        int maxTerms,
        int trimmedCount,
        IReadOnlyList<string> hintCodes)
    {
        Terms = terms;
        ClipSeconds = clipSeconds;
        NeededImages = neededImages;
        MaxTerms = maxTerms;
        TrimmedCount = trimmedCount;
        HintCodes = hintCodes;
    }

    /// <summary>The planned terms in story order; <see cref="PlannedTerm.Slot"/> is the index.</summary>
    public IReadOnlyList<PlannedTerm> Terms { get; }

    /// <summary>Seconds one image stays on screen (<c>video_clip_duration</c>).</summary>
    public int ClipSeconds { get; }

    /// <summary>The images the narration needs.</summary>
    public int NeededImages { get; }

    /// <summary>The cap on terms sent; every one may be called once, so it is also the cost bound's image count.</summary>
    public int MaxTerms { get; }

    /// <summary>How many keywords the cap removed.</summary>
    public int TrimmedCount { get; }

    /// <summary>Advisory hint codes: <see cref="RenderImagePlanner.ScenesTrimmed"/>, <see cref="RenderImagePlanner.ScenesSparse"/>.</summary>
    public IReadOnlyList<string> HintCodes { get; }

    /// <inheritdoc />
    public bool Equals(RenderImagePlan? other) =>
        other is not null
        && ClipSeconds == other.ClipSeconds
        && NeededImages == other.NeededImages
        && MaxTerms == other.MaxTerms
        && TrimmedCount == other.TrimmedCount
        && Terms.SequenceEqual(other.Terms)
        && HintCodes.SequenceEqual(other.HintCodes, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(ClipSeconds);
        hash.Add(NeededImages);
        hash.Add(MaxTerms);
        hash.Add(TrimmedCount);
        foreach (var term in Terms)
        {
            hash.Add(term);
        }

        foreach (var code in HintCodes)
        {
            hash.Add(code, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
