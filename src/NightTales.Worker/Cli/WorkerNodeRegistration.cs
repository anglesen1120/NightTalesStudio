using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Infrastructure.Rendering;

namespace NightTales.Worker.Cli;

/// <summary>
/// The Worker's start-up render-node registration (11 README D13, plan §7): runs
/// <see cref="WorkerNodeSeeder"/> before the health listener starts, so the registry matches
/// <c>RenderNodes:Nodes</c> as soon as the process is up and a misconfigured node fails the boot instead of
/// degrading to unauthenticated polls.
/// </summary>
/// <remarks>
/// <para>
/// The exit-code mapping is the plan's [INFERENCE] convention: a configured node without its
/// <c>RENDER_NODE_&lt;NAME&gt;_API_KEY</c> exits <c>2</c> (the same code the CLI uses for a missing key),
/// any other configuration or database failure exits <c>1</c>. The message names the variable and never its
/// value (11 README S4).
/// </para>
/// <para>
/// It mirrors <c>SeedCommand.Run</c>: the Worker's <c>Program</c> calls it before
/// <c>WebApplication.Run</c>, so the process never announces itself healthy while its registry is wrong.
/// </para>
/// </remarks>
public static class WorkerNodeRegistration
{
    /// <summary>Runs the seeder against the built host; <c>0</c> on success, <c>2</c> for a missing key, <c>1</c> otherwise.</summary>
    /// <param name="services">The built host's service provider.</param>
    /// <param name="cancellationToken">The process's cancellation token.</param>
    public static int Run(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            using var scope = services.CreateScope();
            var seeder = scope.ServiceProvider.GetRequiredService<WorkerNodeSeeder>();

            var registered = seeder.RegisterAsync(cancellationToken).GetAwaiter().GetResult();
            if (!registered.IsError)
            {
                return NodesCommand.Success;
            }

            foreach (var error in registered.Errors)
            {
                Console.Error.WriteLine($"nts-worker: {error.Description}");
            }

            return CodeFor(registered.Errors);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"nts-worker: render node registration failed: {exception.Message}");

            return NodesCommand.Failure;
        }
    }

    /// <summary>
    /// The exit code for a failed seeding: <c>2</c> when the failure is a missing adapter key (the plan §7
    /// convention), <c>1</c> for every other configuration or write failure.
    /// </summary>
    internal static int CodeFor(IReadOnlyList<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return errors.Any(error => string.Equals(
            error.Code,
            WorkerNodeSeeder.MissingApiKeyCode,
            StringComparison.Ordinal))
            ? NodesCommand.Usage
            : NodesCommand.Failure;
    }
}
