using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Api.Http;

/// <summary>
/// Reads an aggregate's <em>current</em> ETag after the handler's unit of work committed (09 README
/// D15): <c>xmin</c> is assigned by PostgreSQL at commit, so the value a handler could return is
/// already stale, and a create answers 201 without an <c>ETag</c> unless somebody reads it back.
/// </summary>
/// <remarks>
/// <para>
/// It lives here and not in <c>Api/Endpoints</c> because the architecture rule
/// <c>Endpoints_DoNotReferenceDbContexts</c> forbids an endpoint type from naming <c>IReadDb</c>
/// (09.1.2-T1); the endpoints take this reader instead. The read is the no-tracking model with the D10
/// tenant filter, so it can only ever see the caller's own row — the same row the command just wrote.
/// </para>
/// <para>
/// 09.4.1 owns <see cref="SeriesAsync"/>; 09.5.1 (episodes) and later mutation routes add their own one
/// line over <see cref="VersionAsync"/> rather than a second reader.
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model.</param>
public sealed class PostCommitETagReader(IReadDb readDb)
{
    /// <summary>The series ETag (<c>"se-&lt;xmin&gt;"</c>), or <see langword="null"/> when the row is not visible.</summary>
    /// <param name="id">The series that was just written.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<string?> SeriesAsync(Guid id, CancellationToken cancellationToken)
        => await VersionAsync<Series>(id, cancellationToken).ConfigureAwait(false) is { } xmin
            ? ETags.Series(xmin)
            : null;

    private async Task<uint?> VersionAsync<TEntity>(Guid id, CancellationToken cancellationToken)
        where TEntity : AggregateRoot
        => await readDb.Query<TEntity>()
            .Where(entity => entity.Id == id)
            .Select(entity => (uint?)entity.Version)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
