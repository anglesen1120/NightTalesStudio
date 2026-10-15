namespace NightTales.Application.Abstractions.Publishing;

/// <summary>A unit reserved on one platform for one Pacific quota day; handed back to <see cref="IQuotaLedger.ReleaseAsync"/>.</summary>
public readonly record struct QuotaReservation(string Platform, DateOnly Day);
