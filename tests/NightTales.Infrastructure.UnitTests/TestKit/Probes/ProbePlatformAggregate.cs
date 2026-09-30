using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.UnitTests.TestKit.Probes;

/// <summary>
/// A test-only aggregate that is <b>not</b> <see cref="IUserOwned"/> (09.2.4-T1): it is what the outbox
/// interceptor must refuse when it raises an event that is not a <see cref="PlatformEventAttribute"/> one.
/// </summary>
/// <remarks>
/// Never mapped in <c>src</c> — <see cref="ProbeModelCustomizer"/> adds it to the model, like
/// <see cref="ProbeAggregate"/>.
/// </remarks>
internal sealed class ProbePlatformAggregate : AggregateRoot
{
    private ProbePlatformAggregate()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public static ProbePlatformAggregate Create(string name) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name,
        };

    /// <summary>Raises an event that may be stored without a user.</summary>
    /// <param name="occurredAt">When it was raised.</param>
    public void RaisePlatformEvent(DateTimeOffset occurredAt) => Raise(new ProbePlatformEvent(occurredAt));

    /// <summary>Raises an event that needs a user although this aggregate has none: the command must fail.</summary>
    /// <param name="occurredAt">When it was raised.</param>
    public void RaiseUnattributedEvent(DateTimeOffset occurredAt) => Raise(new ProbeUnattributedEvent(Id, occurredAt));
}

/// <summary>The mapping of <see cref="ProbePlatformAggregate"/>; applied by <see cref="ProbeModelCustomizer"/> only.</summary>
internal sealed class ProbePlatformAggregateConfiguration : IEntityTypeConfiguration<ProbePlatformAggregate>
{
    public void Configure(EntityTypeBuilder<ProbePlatformAggregate> builder)
    {
        builder.ToTable("probe_platform_aggregates");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
    }
}
