using NightTales.TestKit.Database;
using Xunit;

namespace NightTales.TestKit;

/// <summary>
/// Base class of every test class that needs a real PostgreSQL 17 database (09 README D13). It
/// declares the per-class <see cref="TestDatabase"/> fixture — created by copying the run's migrated
/// template, dropped when the class is over — and it carries the <c>Category=Integration</c> trait,
/// so the unit job can exclude this level with <c>--filter-not-trait "Category=Integration"</c>
/// without needing <c>NTS_TEST_PG</c>.
/// </summary>
/// <remarks>
/// A test class that needs no database must NOT derive from this type: deriving is what asks xUnit
/// for the class database, and the trait is inherited by every derived class.
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public abstract class IntegrationTest(TestDatabase database) : IClassFixture<TestDatabase>
{
    /// <summary>This class's database: an isolated, migrated copy of the run's template.</summary>
    protected TestDatabase Database { get; } = database;
}
