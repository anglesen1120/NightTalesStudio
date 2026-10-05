namespace NightTales.Application.Abstractions.Similarity;

/// <summary>The closed set of content-free similarity failure metric labels.</summary>
public static class SimilarityUnavailableReason
{
    public const string StorageError = "storage_error";
    public const string DbError = "db_error";
    public const string Timeout = "timeout";
}
