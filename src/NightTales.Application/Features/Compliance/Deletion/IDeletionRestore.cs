namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>One line of the operator export/re-apply file (<c>deletions export</c>): ids, dates and wire names only.</summary>
public sealed record DeletionExportRow(
    Guid Id,
    Guid UserId,
    string Scope,
    Guid? TargetId,
    string Reason,
    DateTimeOffset RequestedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? CompletedAt);

/// <summary>The summary a re-apply prints; a non-zero <see cref="Failed"/> or an empty <see cref="Applied"/> on a non-empty file is exit 1.</summary>
public sealed record DeletionReapplyReport(int Applied, int Failed, IReadOnlyList<string> Problems);

/// <summary>The restore-drill operations behind <c>nts-worker deletions export|reapply</c> (13 README D4, YR12).</summary>
public interface IDeletionRestore
{
    /// <summary>Completed after <paramref name="since"/> and every still-open request, ordered <c>completed_at NULLS FIRST, id</c>.</summary>
    Task<IReadOnlyList<DeletionExportRow>> ExportAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Re-executes completed rows, executes open rows already due, re-inserts the rest as pending.</summary>
    Task<DeletionReapplyReport> ReapplyAsync(IReadOnlyList<DeletionExportRow> rows, CancellationToken cancellationToken);
}
