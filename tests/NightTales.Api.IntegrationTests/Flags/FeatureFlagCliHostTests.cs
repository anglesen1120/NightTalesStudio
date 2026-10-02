using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Security;
using NightTales.Worker;
using NightTales.Worker.Cli;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Flags;

/// <summary>Console redirection and Hangfire's global logger must not overlap other test hosts.</summary>
[CollectionDefinition("Process-global host state", DisableParallelization = true)]
public sealed class ProcessGlobalHostState;

/// <summary>
/// QA-FL-2 of 09.9.2-T2 (09 README BD16, 09.9.2 plan §6): a <c>flags</c> invocation may not print a stack
/// trace or a connection detail, whichever part of the process fails. The verb's own stderr line is
/// already typed; these cover the two paths around it — the host logger a verb host would otherwise
/// inherit, and a verb host the process cannot create at all.
/// </summary>
[Collection("Process-global host state")]
public sealed partial class FeatureFlagCliHostTests
{
    /// <summary>
    /// A parseable connection string <c>AddJobs</c> requires while it registers; <c>Build</c> never
    /// connects, so the value is never used.
    /// </summary>
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=5432;Database=nts_flags_host;Username=nts;Password=nts";

    /// <summary>
    /// The verb host has no console sink. The normal Worker's sink appends <c>{Exception}</c> and the EF
    /// Core/Npgsql categories log a failed command with the server and the database, so an inherited sink
    /// would print the stack trace and connection detail the CLI contract forbids next to the operator's
    /// one typed line.
    /// </summary>
    [Fact]
    public void Flags_verb_host_writes_no_host_log_to_the_console()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var consoleOut = Console.Out;
        var consoleError = Console.Error;

        try
        {
            // The console sink resolves Console.Out while the host is built, so the capture starts before it.
            Console.SetOut(stdout);
            Console.SetError(stderr);

            using var host = WorkerApplication.Build(Args("list"));
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NightTales.Flags.Test");

            LogFlagCommandFailed(logger, new InvalidOperationException("Failed to connect to 127.0.0.1:1;Password=nts"));
        }
        finally
        {
            Console.SetOut(consoleOut);
            Console.SetError(consoleError);
        }

        stdout.ToString().ShouldNotContain("   at ", Case.Sensitive, "a verb host never prints a stack trace");
        (stdout.ToString() + stderr.ToString())
            .ShouldNotContain("Password=", Case.Insensitive, "a verb host never echoes a connection string");
        (stdout.ToString() + stderr.ToString())
            .ShouldNotContain("Failed to connect", Case.Sensitive, "a verb host never prints a provider message");
    }

    /// <summary>
    /// A verb host the process cannot create — here a PFX that is not there — is the same operator-facing
    /// failure as an unreachable database: a typed line and exit 2, not the runtime's stack trace with the
    /// secret path in it.
    /// </summary>
    [Fact]
    public void Flags_verb_with_a_host_that_cannot_be_created_exits_2_with_a_typed_error()
    {
        var stderr = new StringWriter();
        var missingKek = Path.Combine(Path.GetTempPath(), "no-such-nts-flags-kek.pfx");

        var exit = FlagsCommand.RunHost(
            [
                FlagsCommand.Verb,
                "list",
                $"--ConnectionStrings:{PersistenceOptions.ConnectionStringName}={UnusedConnectionString}",
                "--environment=Development",
                $"--{TestKek.PathConfigurationKey}={missingKek}",
            ],
            stderr);

        exit.ShouldBe(2, "a host the verb cannot create is not a command-line mistake");
        stderr.ToString().ShouldNotContain("   at ", Case.Sensitive, "never the runtime's stack trace");
        stderr.ToString().ShouldNotContain("no-such-nts-flags-kek.pfx", Case.Sensitive, "never the failing path");
        stderr.ToString().ShouldNotContain(TestKek.PathConfigurationKey, Case.Sensitive, "never the raw host message");
    }

    /// <summary>One host log event that carries an exception and a connection detail.</summary>
    [LoggerMessage(1, LogLevel.Error, "a flag command failed")]
    private static partial void LogFlagCommandFailed(ILogger logger, Exception exception);

    private static string[] Args(string subVerb, params string[] extra)
    {
        var args = new List<string>
        {
            FlagsCommand.Verb,
            subVerb,
            $"--ConnectionStrings:{PersistenceOptions.ConnectionStringName}={UnusedConnectionString}",
            // AddWorkerServices loads the DP_KEK PFX while it registers (09.9.4-T1), so Build needs it.
            $"--{TestKek.PathConfigurationKey}={TestKek.SharedPath}",
            $"--{TestKek.PasswordConfigurationKey}={TestKek.SharedPassword}",
            "--environment=Development",
        };
        args.AddRange(extra);
        return [.. args];
    }
}
