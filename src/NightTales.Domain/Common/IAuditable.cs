namespace NightTales.Domain.Common;

/// <summary>
/// A type of a mutable table: it carries <c>created_at</c> and <c>updated_at</c> (09 README D8,
/// 09 W3 CR-38). Immutable/append-only tables (<c>story_versions</c>, <c>usage_records</c>,
/// <c>moderation_events</c>) stop at <see cref="ICreatedAt"/>.
/// </summary>
public interface IAuditable : ICreatedAt
{
    DateTimeOffset UpdatedAt { get; }
}
