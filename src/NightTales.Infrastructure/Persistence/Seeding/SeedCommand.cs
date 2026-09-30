using Microsoft.Extensions.DependencyInjection;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// The <c>nts-seed</c> command (09.2.5 plan §3.1/§7): builds the host graph, runs
/// <see cref="SeedRunner"/> in one scope and returns the process exit code without starting the web
/// server. The Api and the Worker <c>Program</c> branch to it when <c>--seed</c> is passed.
/// </summary>
/// <remarks>
/// A failure is written to <c>stderr</c> and returned as <c>1</c>; it is never swallowed, so an
/// operator sees a failed seed in a script's exit code. The message is the key-named
/// <see cref="SeedValidationException.Code"/> of a bad catalog.
/// </remarks>
public static class SeedCommand
{
    /// <summary>Runs the seeders against the services of a built host; <c>0</c> on success, <c>1</c> on failure.</summary>
    /// <param name="services">The built host's service provider.</param>
    public static int Run(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            using var scope = services.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<SeedRunner>();

            runner.RunAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"nts-seed failed: {exception.Message}");

            return 1;
        }
    }
}
