using Microsoft.Extensions.Hosting;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Resolves a configured catalog path to a real file (09.2.5-T1). A relative path is tried against the
/// host's content root first — the layout of <c>dotnet run</c> and a container whose working directory
/// is the app root — and then against <see cref="AppContext.BaseDirectory"/>, where the
/// <c>NightTales.Infrastructure</c> project copies the committed <c>seed/</c> tree. An absolute path is
/// used as given.
/// </summary>
/// <remarks>
/// Trying the content root first keeps a developer's override next to the app; the base-directory
/// fallback is what makes <c>dotnet run --project src/NightTales.Api -- --seed</c> work, because
/// <c>dotnet run</c> leaves the working directory at the project directory, while the catalogs live in
/// the build output under <c>seed/</c>.
/// </remarks>
internal static class SeedFiles
{
    /// <summary>The existing file for <paramref name="path"/>, or <c>null</c> when none is found.</summary>
    internal static string? Resolve(string path, IHostEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(environment);

        if (Path.IsPathRooted(path))
        {
            return File.Exists(path) ? path : null;
        }

        var fromContentRoot = Path.Combine(environment.ContentRootPath, path);
        if (File.Exists(fromContentRoot))
        {
            return fromContentRoot;
        }

        var fromBaseDirectory = Path.Combine(AppContext.BaseDirectory, path);
        return File.Exists(fromBaseDirectory) ? fromBaseDirectory : null;
    }
}
