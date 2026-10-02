using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions.Prompts;

namespace NightTales.Infrastructure.Prompts;

/// <summary>
/// The start-up gate of the prompt catalog (02.2.3-T1, 02 README D5/F22): it resolves
/// <see cref="IPromptCatalog"/> while the host starts, so an invalid catalog aborts the boot with a
/// <see cref="PromptCatalogException"/> that names every violation.
/// </summary>
/// <remarks>
/// The gate is the resolution, not the method body: the catalog loads and validates in its constructor,
/// so registering this hosted service is what makes a lazy singleton load at start-up instead of on the
/// first generation call. Both hosts register it through <c>AddPromptCatalog</c>, which is why
/// <c>dotnet run --project src/NightTales.Worker</c> exits non-zero on a deleted preset.
/// </remarks>
/// <param name="catalog">The catalog the host resolves; its construction is the gate.</param>
public sealed class PromptCatalogStartupValidator(IPromptCatalog catalog) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The catalog was already loaded and validated when DI built this service; reading the release
        // keeps the dependency live and makes the gate explicit to a reader.
        _ = catalog.Release;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
