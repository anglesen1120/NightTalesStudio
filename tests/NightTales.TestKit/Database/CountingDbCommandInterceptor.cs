using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NightTales.TestKit.Database;

/// <summary>
/// Records every command the context it is attached to executes, in execution order, and whether two of
/// them were ever in flight at the same time (09.5.3 plan §9, D13: hand-written, no mocking library).
/// </summary>
/// <remarks>
/// <para>
/// The two facts a query-shape test needs come from one interceptor: how many commands a request issued
/// (one command per query — no <c>Include</c>, no N+1, BD16) and whether any pair overlapped, which is
/// what proves the reads ran sequentially on the one <c>DbContext</c> and not through a
/// <c>Task.WhenAll</c> ([RULED — README §B10 WT46]).
/// </para>
/// <para>
/// One instance is attached to one context's options, so its recordings are the commands of the scopes
/// that resolved those options. Calling <see cref="Reset"/> before the request under test keeps a shared
/// host's earlier traffic out of the count.
/// </para>
/// <para>
/// The interceptor is not thread-safe by design: every test here waits for its request to finish before
/// it reads <see cref="Commands"/>, and a concurrent read path would be exactly the bug the
/// overlap flag reports. Reference equality is used for the in-flight set, because a <c>DbCommand</c>
/// instance is created per execution and carries no value equality.
/// </para>
/// </remarks>
public sealed class CountingDbCommandInterceptor : DbCommandInterceptor
{
    private readonly List<ExecutedCommand> _commands = [];
    private readonly HashSet<DbCommand> _inFlight = [];

    /// <summary>The commands recorded so far, in the order they started.</summary>
    public IReadOnlyList<ExecutedCommand> Commands => _commands;

    /// <summary>How many commands were executed; one command is one round trip.</summary>
    public int Count => _commands.Count;

    /// <summary>
    /// True when a command started while another was still in flight — the observed
    /// <c>Task.WhenAll</c>/<c>Task</c> pair EF forbids on a single context (WT46).
    /// </summary>
    public bool ObservedConcurrentExecution => _commands.Exists(command => command.ActiveWhenStarted > 0);

    /// <summary>Forgets every recording, so the next request is measured on its own.</summary>
    public void Reset()
    {
        _commands.Clear();
        _inFlight.Clear();
    }

    /// <summary>The command texts recorded so far, for a readable assertion message.</summary>
    public IReadOnlyList<string> Texts => [.. _commands.Select(command => command.Text)];

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Begin(command);

        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Begin(command);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        End(command);

        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        End(command);

        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Begin(command);

        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Begin(command);

        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        End(command);

        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        End(command);

        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Begin(command);

        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Begin(command);

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        End(command);

        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        End(command);

        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        End(command);

        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        End(command);

        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    /// <summary>Records the command and how many were in flight when it started.</summary>
    private void Begin(DbCommand command)
    {
        _commands.Add(new ExecutedCommand(command.CommandText, _inFlight.Count));
        _inFlight.Add(command);
    }

    /// <summary>Marks the command as finished, whatever its outcome.</summary>
    private void End(DbCommand command) => _inFlight.Remove(command);
}

/// <summary>
/// One executed command: its text and how many other commands were already in flight when it started
/// (0 for a command that ran alone).
/// </summary>
/// <param name="Text">The SQL EF sent, which names the table it read.</param>
/// <param name="ActiveWhenStarted">The in-flight count at start; anything above 0 is an overlap.</param>
public sealed record ExecutedCommand(string Text, int ActiveWhenStarted);
