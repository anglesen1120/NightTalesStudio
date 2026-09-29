using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Time;
using NightTales.TestKit.Auth;

namespace NightTales.TestKit.Database;

/// <summary>
/// Builds the real <see cref="AppDbContext"/> / <see cref="ReadDbContext"/> for a test connection
/// string, with the provider options <c>AddPersistence</c> uses (09 README D8: Npgsql + snake_case
/// naming) plus the audit interceptor, so a test row gets its <c>created_at</c>/<c>updated_at</c> the
/// same way production rows do (09 W3 CR-38). Both the template rebuild of
/// <see cref="PostgresFixture"/> and every <see cref="TestDatabase"/> go through here, so the tests
/// always exercise the production model and exactly the migrations the Infrastructure assembly
/// compiles.
/// </summary>
public static class TestDbContextFactory
{
    /// <summary>
    /// Options for the tracked write model; it is the context that owns the migrations.
    /// <paramref name="clock"/> defaults to the real <see cref="SystemClock"/>, so a test that needs a
    /// deterministic stamp passes a <c>FakeClock</c>.
    /// </summary>
    public static DbContextOptions<AppDbContext> AppOptions(string connectionString, IClock? clock = null) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new AuditableSaveChangesInterceptor(clock ?? new SystemClock()))
            .Options;

    /// <summary>Options for the no-tracking read model (no migrations, no <c>SaveChanges</c>).</summary>
    public static DbContextOptions<ReadDbContext> ReadOptions(string connectionString) =>
        new DbContextOptionsBuilder<ReadDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

    /// <summary>
    /// Creates a write context. <paramref name="currentUser"/> defaults to an authenticated no-op
    /// (<see cref="TestCurrentUser"/> with no id); 09.2.3-T1 owns the tenant filter that reads it.
    /// </summary>
    public static AppDbContext CreateAppContext(
        string connectionString,
        ICurrentUser? currentUser = null,
        IClock? clock = null) =>
        new(AppOptions(connectionString, clock), currentUser ?? new TestCurrentUser());

    /// <summary>Creates a read context for <paramref name="connectionString"/>.</summary>
    public static ReadDbContext CreateReadContext(string connectionString, ICurrentUser? currentUser = null) =>
        new(ReadOptions(connectionString), currentUser ?? new TestCurrentUser());
}
