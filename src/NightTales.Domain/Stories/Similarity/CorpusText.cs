namespace NightTales.Domain.Stories.Similarity;

/// <summary>A source's untrusted extracted prose. Its diagnostic representation never includes prose.</summary>
public sealed record CorpusText(Guid SourceId, string Text)
{
    public override string ToString() => $"CorpusText {{ SourceId = {SourceId} }}";
}
