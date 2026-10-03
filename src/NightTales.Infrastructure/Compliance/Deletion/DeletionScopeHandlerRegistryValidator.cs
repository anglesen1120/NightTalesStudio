using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Infrastructure.Compliance.Deletion;

/// <summary>
/// Fails the Worker start when a non-deferred scope has no handler (13.3.1 plan §7.1, K21) or when a scope has more
/// than one: a missing handler must surface at deploy, never as a silently missed legal deadline, and a duplicate
/// would make the registry pick one by registration order. It resolves handlers only; no database call.
/// </summary>
public sealed class DeletionScopeHandlerRegistryValidator(
    IServiceScopeFactory scopes,
    DataDeletionOptions options) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IDeletionScopeHandler>().ToList();
        var registered = scope.ServiceProvider.GetRequiredService<IDeletionScopeHandlerRegistry>().RegisteredScopes;
        var deferred = options.DeferredScopeSet();

        var duplicated = handlers
            .GroupBy(handler => handler.Scope)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.ToWire())
            .ToList();

        if (duplicated.Count > 0)
        {
            throw new InvalidOperationException(
                $"More than one IDeletionScopeHandler is registered for the deletion scope(s) [{string.Join(", ", duplicated)}]; "
                + "each scope has exactly one handler (13.3.1 plan K21).");
        }

        var missing = Enum.GetValues<DeletionScope>()
            .Where(candidate => !deferred.Contains(candidate) && !registered.Contains(candidate))
            .Select(candidate => candidate.ToWire())
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"No IDeletionScopeHandler is registered for the required deletion scope(s) [{string.Join(", ", missing)}]. "
                + $"Register the handler, or list the scope in '{DataDeletionOptions.SectionName}:{nameof(DataDeletionOptions.DeferredScopes)}' "
                + "while its owner has not landed (13.3.1 plan K21).");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
