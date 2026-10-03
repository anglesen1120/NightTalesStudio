namespace NightTales.Application.Abstractions.Money;

/// <summary>The identity of one credit reservation (02 README D21); a UUIDv7 wrapped so it is never confused with another id.</summary>
/// <param name="Value">The reservation's id.</param>
public readonly record struct ReservationId(Guid Value);
