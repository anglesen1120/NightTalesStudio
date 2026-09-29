using Xunit;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit;
using Shouldly;

namespace NightTales.Infrastructure.UnitTests.Persistence;

public sealed class ReadDbContextTests
{
    [Fact]
    public async Task ReadDbContext_SaveChanges_ThrowsInvalidOperationException()
    {
        using var context = new ReadDbContext(ProbeContexts.ReadOptions(ProbeContexts.UnusedConnectionString), new AnonymousCurrentUser());

        Should.Throw<InvalidOperationException>(() => context.SaveChanges()).Message.ShouldBe("read-only context");
        Should.Throw<InvalidOperationException>(() => context.SaveChanges(acceptAllChangesOnSuccess: false)).Message.ShouldBe("read-only context");
        (await Should.ThrowAsync<InvalidOperationException>(() => context.SaveChangesAsync(CancellationToken.None)))
            .Message.ShouldBe("read-only context");
        (await Should.ThrowAsync<InvalidOperationException>(() => context.SaveChangesAsync(acceptAllChangesOnSuccess: false, CancellationToken.None)))
            .Message.ShouldBe("read-only context");
    }

    [Fact]
    public void ReadDbContext_DefaultsToNoTracking()
    {
        using var context = new ReadDbContext(ProbeContexts.ReadOptions(ProbeContexts.UnusedConnectionString), new AnonymousCurrentUser());

        context.ChangeTracker.QueryTrackingBehavior.ShouldBe(QueryTrackingBehavior.NoTracking);
    }
}
