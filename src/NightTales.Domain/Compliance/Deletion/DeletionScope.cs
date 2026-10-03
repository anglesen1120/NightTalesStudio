namespace NightTales.Domain.Compliance.Deletion;

/// <summary>What a deletion duty removes (13.3.1 plan §3.1, 13 README D4).</summary>
public enum DeletionScope
{
    Account,
    Channel,
    Episode,
}
