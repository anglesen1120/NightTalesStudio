using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// Builders write real rows through the write model (09 README D13): a test user is an actual
/// <c>users</c> row, so later aggregates can hold foreign keys to it.
/// </summary>
public sealed class UserBuilderTests : IntegrationTest
{
    public UserBuilderTests(TestDatabase database)
        : base(database)
    {
    }

    [Fact]
    public async Task UserBuilder_CreatesUserRow_VisibleThroughReadDb()
    {
        var userId = Guid.CreateVersion7();
        var currentUser = new TestCurrentUser { Id = userId, Role = PlatformRole.Admin, Actor = ActorType.Admin };

        var user = await Database
            .CreateUserBuilder(currentUser)
            .CreateAsync(userId, "owner@nighttales.test", TestContextToken.Value);

        user.Id.ShouldBe(userId);

        await using var read = Database.CreateReadDbContext();
        var row = await read
            .Query<AppUser>()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, TestContextToken.Value);

        row.ShouldNotBeNull("the builder inserts a real users row through AppDbContext");
        row.Email.ShouldBe("owner@nighttales.test");
        row.NormalizedEmail.ShouldBe("OWNER@NIGHTTALES.TEST");
        row.EmailConfirmed.ShouldBeTrue();

        // And the row is in the database itself, not only in the change tracker of the write context.
        (await Database.QueryStringsAsync("SELECT email FROM users", TestContextToken.Value))
            .ShouldContain("owner@nighttales.test");
    }
}
