namespace NightTales.Domain.Common;

/// <summary>
/// A persisted type that carries the immutable creation timestamp (09 README D8, 09 W3 CR-38).
/// One EF convention maps the property to <c>created_at timestamptz NOT NULL</c> and
/// <c>AuditableSaveChangesInterceptor</c> stamps it from <see cref="IClock"/> — never a hand-written
/// configuration and never a factory that writes the column itself unless it already received
/// <c>now</c>.
/// </summary>
public interface ICreatedAt
{
    DateTimeOffset CreatedAt { get; }
}
