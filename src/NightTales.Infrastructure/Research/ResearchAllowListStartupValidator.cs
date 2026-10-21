using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Research;

/// <summary>
/// Loads and validates the research allow-list when the host starts (02.7.2-T1). A broken file fails the boot, because
/// resolving <see cref="IResearchAllowList"/> throws. Zero approved sites is not an error: the source finder stays
/// disabled and the start logs a warning.
/// </summary>
public sealed partial class ResearchAllowListStartupValidator(
    IServiceProvider services,
    ILogger<ResearchAllowListStartupValidator> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var allowList = services.GetRequiredService<IResearchAllowList>();
        var approved = allowList.Approved(Language.Vi).Concat(allowList.Approved(Language.En)).DistinctBy(s => s.Domain).Count();

        LogLoaded(logger, allowList.Hash, approved);
        if (approved == 0)
        {
            LogNoneApproved(logger);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Research allow-list loaded: sha256 {Hash}, {Approved} approved site(s).")]
    private static partial void LogLoaded(ILogger logger, string hash, int approved);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No research site is approved; the source finder has no sites to search (02.7.2-T1).")]
    private static partial void LogNoneApproved(ILogger logger);
}
