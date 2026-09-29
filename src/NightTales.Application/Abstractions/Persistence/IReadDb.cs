namespace NightTales.Application.Abstractions.Persistence;

/// <summary>
/// The read path (09 README D8). Query handlers project straight to DTOs; the returned queryables
/// are never tracked and expose no <c>SaveChanges</c>.
/// </summary>
public interface IReadDb
{
    IQueryable<TEntity> Query<TEntity>()
        where TEntity : class;
}
