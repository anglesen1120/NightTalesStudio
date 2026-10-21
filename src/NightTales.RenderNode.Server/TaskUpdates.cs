namespace NightTales.RenderNode.Server;

/// <summary>
/// The store update the core uses everywhere: merge the observation into the stored row
/// (<see cref="RenderTaskRecord.Merge"/>) and, when the store reports a conflict, re-read and retry exactly
/// once — the "UpdateAsync conflict (poller vs cancel)" rule of 11.3.5-T1's failure table. A second conflict
/// leaves the store untouched and is not an error for the caller, which already answered its client: the
/// poller owns the next write.
/// </summary>
internal static class TaskUpdates
{
    internal static async Task<bool> UpdateAsync(ITaskStore store, RenderTaskRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(record);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await store.UpdateAsync(record, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            var stored = await store.TryGetAsync(record.TaskId, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return false;
            }

            record = stored.Merge(record);
        }

        return false;
    }
}
