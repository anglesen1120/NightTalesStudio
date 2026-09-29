using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NightTales.Application.Abstractions.Persistence;

/// <summary>
/// The tracked write model (09 README D8). Command handlers mutate aggregates through it; only
/// <c>UnitOfWorkBehavior</c> (09 README D5) and the seeders call <see cref="SaveChangesAsync"/>.
/// Every owning feature adds its own <c>DbSet&lt;T&gt;</c> properties to this interface.
/// </summary>
public interface IAppDbContext
{
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
