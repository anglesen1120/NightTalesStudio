using ErrorOr;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Identity;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// The local breached-password check of <c>Security:BreachedPasswords:Mode = list</c> (09.3.1-T1): the
/// SHA-1 of the candidate is binary-searched in the bundled top-list resource, so dev and CI need no
/// network and no privacy decision.
/// </summary>
/// <remarks>
/// <para>
/// The resource is embedded in this assembly and loaded once, sorted, for the process lifetime — hence
/// a singleton. Only the first <c>Security:BreachedPasswords:TopListSize</c> entries are honoured (the
/// list is ranked, most common first).
/// </para>
/// <para>
/// A list that is missing, empty or holds only comment lines is a failure at start-up, never a silent
/// "accept everything": the options validation calls <see cref="HasUsableBundledList"/> and the
/// constructor throws as well, so a host that bypasses the options validation still cannot start with a
/// check that matches nothing (09.3.1-T1 failure handling).
/// </para>
/// <para>
/// Replace the bundled file with the real top-list of the breached-password provider when its terms
/// are confirmed (09.3.1-T2, S); the lookup needs no change.
/// </para>
/// </remarks>
/// <param name="options">The breached-password options (the top-list size).</param>
public sealed class TopListPasswordChecker(IOptions<BreachedPasswordOptions> options) : IBreachedPasswordChecker
{
    /// <summary>The logical name of the bundled resource (set in the project file).</summary>
    public const string ResourceName = "NightTales.Infrastructure.Identity.Resources.breached-passwords.txt";

    /// <summary>The failure message of a list with no usable entry; a constant, so no file content leaks.</summary>
    internal const string EmptyListMessage =
        "The bundled breached-password list holds no usable entry; a list-mode check that matches nothing "
        + "would accept every password, so this host refuses to start.";

    private readonly string[] _hashes = LoadBundled(options.Value.TopListSize);

    /// <summary>
    /// Whether the bundled resource exists and holds at least one usable entry — the start-up fail-fast
    /// of <c>list</c> mode, which the composition root runs through the options validation.
    /// </summary>
    /// <remarks>
    /// It stops at the first entry instead of parsing and sorting the whole file: the singleton's
    /// constructor is the one full load (QA P1), and a missing resource or an empty/comment-only file
    /// still answers <c>false</c> whether the operator fixes the deployment or the file.
    /// </remarks>
    public static bool HasUsableBundledList()
    {
        using var stream = OpenBundledStream();
        if (stream is null)
        {
            return false;
        }

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && trimmed[0] is not '#')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the ranked list from <paramref name="stream"/>: one hash per line, <c>#</c> comment lines
    /// skipped, at most <paramref name="topListSize"/> entries, returned ascending ordinal.
    /// </summary>
    /// <param name="stream">The list's bytes; the caller owns and disposes it.</param>
    /// <param name="topListSize">The number of ranked entries to honour; at least one.</param>
    /// <exception cref="InvalidOperationException">The stream holds no usable entry.</exception>
    /// <remarks>
    /// Public because the empty and comment-only cases are unit-tested from another assembly — this
    /// solution has no <c>InternalsVisibleTo</c> (the same reason <c>ErrorCatalogue</c> is public).
    /// </remarks>
    public static string[] Load(Stream stream, int topListSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(topListSize, 1);

        using var reader = new StreamReader(stream);
        var hashes = new List<string>(topListSize);
        while (hashes.Count < topListSize && reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#')
            {
                // The bundled file documents its own provenance in leading '#' comment lines.
                continue;
            }

            hashes.Add(trimmed.ToUpperInvariant());
        }

        if (hashes.Count == 0)
        {
            throw new InvalidOperationException(EmptyListMessage);
        }

        var array = hashes.ToArray();
        Array.Sort(array, StringComparer.Ordinal);

        return array;
    }

    /// <inheritdoc />
    public Task<ErrorOr<bool>> IsBreachedAsync(string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);

        var index = Array.BinarySearch(_hashes, PasswordRangeKey.Full(password), StringComparer.Ordinal);

        return Task.FromResult<ErrorOr<bool>>(index >= 0);
    }

    private static string[] LoadBundled(int topListSize)
    {
        using var stream = OpenBundledStream()
            ?? throw new InvalidOperationException(
                $"The bundled breached-password list '{ResourceName}' is missing from "
                + $"{typeof(TopListPasswordChecker).Assembly.GetName().Name}; Security:BreachedPasswords:Mode = list "
                + "needs it and must fail at start-up instead of accepting every password.");

        return Load(stream, topListSize);
    }

    private static Stream? OpenBundledStream() =>
        typeof(TopListPasswordChecker).Assembly.GetManifestResourceStream(ResourceName);
}
