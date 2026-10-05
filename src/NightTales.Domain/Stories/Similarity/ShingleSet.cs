namespace NightTales.Domain.Stories.Similarity;

/// <summary>Creates a distinct set from window hashes.</summary>
public static class ShingleSet
{
    /// <summary>Deduplicates hashes without altering their values.</summary>
    public static IReadOnlySet<ulong> From(ulong[] hashes) => new HashSet<ulong>(hashes);
}
