namespace NightTales.Domain.Common;

/// <summary>UTC clock port (09 README D3). Domain code never reads the clock itself.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
