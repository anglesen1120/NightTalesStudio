using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;

namespace NightTales.Infrastructure.Compliance;

/// <summary>
/// Writes <c>moderation_events</c> rows (02 README D12). Every call opens its own scope pinned to the run's owner
/// and saves one row, so the row is committed independently of the job's transaction. The row is evidence, not
/// control flow: a failed write is logged (the exception type only, D19) and the run outcome is unchanged.
/// </summary>
/// <param name="scopeFactory">Creates the fresh scope per write.</param>
/// <param name="clock">The row's creation instant.</param>
/// <param name="log">The failure log.</param>
public sealed partial class ModerationEventWriter(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ModerationEventWriter> log) : IModerationEventWriter
{
    /// <summary>How long one write may take, independent of the caller's token: a cancelled job still keeps its evidence.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task WriteAsync(
        Guid userId,
        Guid? episodeId,
        ModerationSource source,
        ModerationStage stage,
        IReadOnlyCollection<string> categories,
        string? ownText,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(categories);

        try
        {
            using var deadline = new CancellationTokenSource(WriteTimeout);

            await using var scope = scopeFactory.CreateAsyncScope();
            using var actor = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

            // ModerationEvent.Record truncates the excerpt to 300 characters.
            db.ModerationEvents.Add(ModerationEvent.Record(
                userId,
                source,
                stage.ToWireName(),
                categories,
                clock.UtcNow,
                episodeId,
                ownText));

            await db.SaveChangesAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogWriteFailed(ex.GetType().Name, stage.ToWireName());
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "moderation_events row not written for stage {Stage}: {ExceptionType}")]
    private partial void LogWriteFailed(string exceptionType, string stage);
}
