using Microsoft.EntityFrameworkCore;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// Registers the Night Tales model conventions. Both contexts call it from their
/// <c>ConfigureConventions</c> override, so the write and the read model stay identical
/// (09 README D8, C1).
/// </summary>
public static class ModelConfigurationBuilderExtensions
{
    public static ModelConfigurationBuilder ApplyNightTalesConventions(this ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Every DateTimeOffset is a timestamptz (09 README D8, C7).
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");

        configurationBuilder.Conventions.Add(_ => new EntityIdConvention());
        configurationBuilder.Conventions.Add(_ => new AggregateRootConvention());
        configurationBuilder.Conventions.Add(_ => new EnumCheckConvention());

        return configurationBuilder;
    }
}
