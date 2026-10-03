namespace NightTales.Domain.Compliance.Deletion;

/// <summary>What one scope handler run removed (13 README YR7: this shape is frozen; 06.4.3-T1 adapts to it).</summary>
/// <param name="ObjectsDeleted">Stored objects the run removed; <c>0</c> on an idempotent re-run.</param>
/// <param name="RowsAffected">Database rows the run removed or scrubbed.</param>
public sealed record DeletionResult(int ObjectsDeleted, int RowsAffected);
