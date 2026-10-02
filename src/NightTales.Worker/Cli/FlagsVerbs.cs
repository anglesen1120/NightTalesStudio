using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Features.Platform.Flags.Commands;
using NightTales.Application.Features.Platform.Flags.Queries;

namespace NightTales.Worker.Cli;

/// <summary>
/// The <c>flags</c> verb of the <c>nts-worker</c> binary (09 README BD13, 09.9.2 plan §6): <c>list</c>
/// prints every flag, <c>set &lt;key&gt; on|off</c> flips one through the Mediator command that owns the
/// row, the audit row and the change notification.
/// </summary>
/// <remarks>
/// <para>
/// It runs in the same verb host as 11.3.3-T1's <c>nodes</c> verbs: the Worker has <b>no web listener</b>,
/// the operator boundary is the host and its secret path, and the command it dispatches is
/// <c>[AllowAnonymousRequest]</c> because no HTTP principal exists on this path (D4). <c>Program</c>
/// branches here before it maps a route or starts Kestrel, so a verb invocation binds no port.
/// </para>
/// <para>
/// Exit codes are the task's contract: <c>0</c> success (including a set that changed nothing),
/// <c>1</c> validation (a bad key shape, an unknown key, a malformed command line) and <c>2</c> a database
/// or transport failure. Nothing here prints a connection string, a stack trace or a user value: a failure
/// is named by its exception type or its error code only (09 README BD16).
/// </para>
/// </remarks>
public static class FlagsCommand
{
    /// <summary>The verb <c>Program</c> looks for (the <c>n</c> in <c>flags</c> is not a switch).</summary>
    public const string Verb = "flags";

    /// <summary>The fixed marker <c>flags list</c> prints where an S-phase reader would print rules.</summary>
    public const string RulesMarker = "rules: (unused in P)";

    private const string ListVerb = "list";
    private const string SetVerb = "set";
    private const string Usage =
        "usage: nts-worker flags list" + "\n"
        + "       nts-worker flags set <key> on|off";

    /// <summary>Whether <paramref name="args"/> asks for a <c>flags</c> verb instead of starting a host.</summary>
    /// <param name="args">The process arguments.</param>
    public static bool IsFlagsVerb(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Verb, StringComparison.Ordinal);

    /// <summary>
    /// Builds the verb host and runs the verb, so that a host the process cannot create — a missing
    /// secret, an unusable setting — fails the way a database the verb cannot reach does: the exception's
    /// type on stderr and exit <c>2</c>, never the runtime's stack trace (09.9.2-T2 §Operator CLI).
    /// </summary>
    /// <param name="args">The process arguments, the verb included.</param>
    /// <param name="error">Where a failure goes; <c>Console.Error</c> when omitted.</param>
    public static int RunHost(string[] args, TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var stderr = error ?? Console.Error;

        WebApplication host;

        try
        {
            host = WorkerApplication.Build(args);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: the host's own message names the setting or the file that failed (BD16).
            return Fail(stderr, $"nts-worker flags failed: {exception.GetType().Name}", 2);
        }

        return Run(host.Services, args, error: stderr);
    }

    /// <summary>Runs the verb and returns the process exit code (09.9.2-T2 §Operator CLI).</summary>
    /// <param name="services">The built Worker host's service provider.</param>
    /// <param name="args">The process arguments, the verb included.</param>
    /// <param name="output">Where the listing goes; <c>Console.Out</c> when omitted.</param>
    /// <param name="error">Where a failure goes; <c>Console.Error</c> when omitted.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public static int Run(
        IServiceProvider services,
        IReadOnlyList<string> args,
        TextWriter? output = null,
        TextWriter? error = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        if (args.Count < 2)
        {
            return Fail(stderr, Usage);
        }

        var subVerb = args[1];

        if (string.Equals(subVerb, ListVerb, StringComparison.Ordinal))
        {
            return args.Count == 2 ? List(services, stdout, stderr, ct) : Fail(stderr, Usage);
        }

        if (string.Equals(subVerb, SetVerb, StringComparison.Ordinal))
        {
            return args.Count == 4 ? Set(services, args[2], args[3], stdout, stderr, ct) : Fail(stderr, Usage);
        }

        return Fail(stderr, Usage);
    }

    /// <summary>Prints one line per flag: key, state and the fixed rules marker.</summary>
    private static int List(IServiceProvider services, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            var result = sender.Send(new ListFeatureFlagsQuery(), ct).AsTask().GetAwaiter().GetResult();
            if (result.IsError)
            {
                // The only error a listing can produce is the database refusing the read: exit 2
                // (09.9.2 plan §6), never the validation code the set verb uses.
                return Fail(stderr, $"nts-worker flags list failed: {Codes(result.Errors)}", 2);
            }

            stdout.WriteLine($"{"key",-32} {"state",-6} rules");

            foreach (var flag in result.Value)
            {
                stdout.WriteLine($"{flag.Key,-32} {(flag.Enabled ? "on" : "off"),-6} {RulesMarker}");
            }

            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a provider message can carry the connection string (09 README D17).
            return Fail(stderr, $"nts-worker flags list failed: {exception.GetType().Name}", 2);
        }
    }

    /// <summary>Flips one flag through <see cref="SetFeatureFlagCommand"/>.</summary>
    private static int Set(
        IServiceProvider services,
        string key,
        string state,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (!TryParseState(state, out var enabled))
        {
            return Fail(stderr, $"'{state}' is not on|off." + Environment.NewLine + Usage);
        }

        try
        {
            using var scope = services.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            var result = sender
                .Send(new SetFeatureFlagCommand(key, enabled), ct)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            if (result.IsError)
            {
                // A key that is malformed and a key no row has are both "fix the command" failures; a
                // database that cannot answer is exit 2 (09.9.2-T2 §Operator CLI).
                var code = Codes(result.Errors);

                return IsValidation(result.Errors) ? Fail(stderr, $"nts-worker flags set failed: {code}") : 2;
            }

            stdout.WriteLine(
                $"{result.Value.Key} {(result.Value.Enabled ? "on" : "off")} "
                + $"({(result.Value.Changed ? "changed" : "unchanged")})");

            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Neither the message nor a stack trace reaches the operator: a provider message can carry the
            // connection string (09 README BD16/D17).
            return Fail(stderr, $"nts-worker flags set failed: {exception.GetType().Name}", 2);
        }
    }

    /// <summary>Whether <paramref name="state"/> is the operator's <c>on</c> or <c>off</c>.</summary>
    private static bool TryParseState(string state, out bool enabled)
    {
        if (string.Equals(state, "on", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;

            return true;
        }

        if (string.Equals(state, "off", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;

            return true;
        }

        enabled = false;

        return false;
    }

    /// <summary>Whether every error is one the operator fixes by correcting the command line.</summary>
    private static bool IsValidation(IReadOnlyList<ErrorOr.Error> errors)
    {
        foreach (var error in errors)
        {
            if (error.Type != ErrorOr.ErrorType.Validation
                && !string.Equals(error.Code, "not_found", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string Codes(IReadOnlyList<ErrorOr.Error> errors) =>
        string.Join(", ", errors.Select(error => error.Code));

    private static int Fail(TextWriter stderr, string message, int code = 1)
    {
        stderr.WriteLine(message);

        return code;
    }
}
