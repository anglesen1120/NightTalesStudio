using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Audit;
using NightTales.Infrastructure.UnitTests.TestKit;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The <c>EfAuditWriter</c> guard of 09.3.6-T1 (09 README BD15, D5): the port refuses to write outside a
/// transaction, so an audit row can never be persisted apart from the change it records. No database is
/// needed — the guard runs before the row is added, and the happy path is proven over a real transaction
/// by the Api integration tests.
/// </summary>
public sealed class EfAuditWriterTests
{
    [Fact]
    public async Task IAuditWriter_WithoutUnitOfWork_Throws()
    {
        using var context = new AppDbContext(
            ProbeContexts.AppOptions(ProbeContexts.UnusedConnectionString),
            new AnonymousCurrentUser());

        var writer = new EfAuditWriter(context);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await writer.WriteAsync(Entry(), CancellationToken.None));

        exception.Message.ShouldContain("transaction");
        context.ChangeTracker.HasChanges().ShouldBeFalse("the guard runs before the row is added");
    }

    private static AuditEntry Entry() => new(
        ActorUserId: null,
        ActorType: ActorType.System,
        SubjectUserId: null,
        Action: "probe.no_transaction",
        Category: Category.Support,
        Visibility: Visibility.Internal,
        EntityType: null,
        EntityId: null,
        DataJson: "{}",
        Ip: null,
        UserAgent: null,
        ActorLabel: null,
        OccurredAt: new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero));
}
