using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NightTales.Application.Abstractions;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Auth;

namespace NightTales.TestKit.Realtime;

/// <summary>
/// Answers the statements a <c>PgNotifyRealtimeNotifier</c> issues and records them, so a unit test can
/// prove the notifier's exact SQL contract without a database (09.8.1-T1 plan §9).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="AppDbContext"/> is a real one over the real Npgsql provider model, so
/// <c>Database.ExecuteSqlRawAsync</c> walks the production path; only the two provider calls that would
/// touch a server are intercepted. The connection never opens
/// (<see cref="DbConnectionInterceptor.ConnectionOpeningAsync"/> suppresses it) and the command never
/// executes (<see cref="DbCommandInterceptor.NonQueryExecutingAsync"/> answers <c>0</c>), which is what
/// makes "exactly one <c>SELECT pg_notify</c>, no <c>BEGIN</c>, no <c>SaveChanges</c>" observable with
/// nothing running.
/// </para>
/// <para>
/// The placeholder connection string points at a port nothing listens on: if the interception ever
/// stops working, the test fails on a connection error instead of silently passing.
/// </para>
/// </remarks>
public sealed class RecordingDbConnectionFactory
{
    /// <summary>A syntactically valid connection string that can never be reached.</summary>
    public const string PlaceholderConnectionString =
        "Host=127.0.0.1;Port=1;Database=nts-notify-recording;Username=nts;Password=nts;Timeout=1";

    private readonly ConnectionBlocker _blocker = new();
    private readonly CommandRecorder _recorder = new();

    /// <summary>Every command the context was asked to execute, in order.</summary>
    public IReadOnlyList<RecordedCommand> Commands => _recorder.Commands;

    /// <summary>The single command recorded so far, or a failure when the count is not one.</summary>
    /// <exception cref="InvalidOperationException">When more than one command was recorded.</exception>
    public RecordedCommand SingleCommand => _recorder.Commands.Count == 1
        ? _recorder.Commands[0]
        : throw new InvalidOperationException(
            $"Expected exactly one recorded command, found {_recorder.Commands.Count}: "
            + string.Join(" | ", _recorder.Commands.Select(command => command.CommandText)));

    /// <summary>Options of a context whose provider calls are recorded and answered locally.</summary>
    public DbContextOptions<AppDbContext> BuildOptions() => new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(PlaceholderConnectionString)
        .UseSnakeCaseNamingConvention()
        .AddInterceptors(_recorder, _blocker)
        .Options;

    /// <summary>A write context over this factory's recording connection.</summary>
    /// <param name="currentUser">The actor; defaults to an authenticated no-op (09.2.3-T1's tenant filter).</param>
    public AppDbContext CreateContext(ICurrentUser? currentUser = null) =>
        new(BuildOptions(), currentUser ?? new TestCurrentUser());

    /// <summary>One statement the notifier issued, with the values of its parameters.</summary>
    /// <param name="CommandText">The SQL as the provider would have sent it (parameters still named).</param>
    /// <param name="ParameterValues">The parameter values, in the order the provider held them.</param>
    public sealed record RecordedCommand(string CommandText, IReadOnlyList<object?> ParameterValues)
    {
        /// <summary>The statement's single parameter value, or a failure when there is not exactly one.</summary>
        /// <exception cref="InvalidOperationException">When the statement does not carry exactly one parameter.</exception>
        public string SingleParameter => ParameterValues.Count == 1
            ? ParameterValues[0] as string
                ?? throw new InvalidOperationException(
                    $"The command's parameter is {ParameterValues[0]?.GetType().Name ?? "null"}, not a string.")
            : throw new InvalidOperationException(
                $"Expected exactly one parameter, found {ParameterValues.Count}.");
    }

    /// <summary>Records every command and answers it with <c>0</c> rows without touching a server.</summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly List<RecordedCommand> _commands = [];

        public List<RecordedCommand> Commands => _commands;

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Record(command);

            return InterceptionResult<int>.SuppressWithResult(0);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);

            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }

        private void Record(DbCommand command) =>
            _commands.Add(new RecordedCommand(
                command.CommandText,
                [.. command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value)]));
    }

    /// <summary>Suppresses every attempt to open a connection, so nothing ever reaches a socket.</summary>
    private sealed class ConnectionBlocker : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }
}
