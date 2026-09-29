using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts real <c>users</c> rows (09 README D13): a test user is an actual Identity row, so the
/// foreign keys of every later aggregate hold. The row goes through <see cref="AppDbContext"/> with
/// the given <see cref="TestCurrentUser"/>, exactly like production code — the write model is not
/// bypassed with raw SQL.
/// </summary>
/// <remarks>
/// The extra <c>users</c> columns (role, status, …) are 09.2.2-T1's; this builder fills the columns
/// Identity itself needs (id, user name, e-mail, the normalized duplicates and the stamps).
/// </remarks>
public sealed class UserBuilder(TestDatabase database, TestCurrentUser currentUser)
{
    /// <summary>The actor the write context runs as.</summary>
    public TestCurrentUser CurrentUser => currentUser;

    /// <summary>
    /// Inserts one user and returns it. <paramref name="id"/> defaults to a new UUIDv7 and
    /// <paramref name="email"/> to a unique address derived from the id, so repeated calls in one
    /// database never collide on Identity's unique <c>normalized_user_name</c> index.
    /// </summary>
    public async Task<AppUser> CreateAsync(
        Guid? id = null,
        string? email = null,
        CancellationToken ct = default)
    {
        var userId = id ?? Guid.CreateVersion7();
        var address = email ?? $"{userId:N}@nighttales.test";

        var user = new AppUser
        {
            Id = userId,
            UserName = address,
            NormalizedUserName = address.ToUpperInvariant(),
            Email = address,
            NormalizedEmail = address.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        };

        await using var context = database.CreateAppDbContext(currentUser);
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        return user;
    }
}
