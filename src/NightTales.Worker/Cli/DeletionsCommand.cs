using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Features.Compliance.Deletion;

namespace NightTales.Worker.Cli;

/// <summary>
/// The operator pair of the restore drill (13.3.1 plan §5.3, 13 README D4/YR12, 11.6.1-T1): <c>nts-worker deletions
/// export --since &lt;iso8601&gt; --out &lt;path&gt;</c> (read-only, against the live database before a restore) and
/// <c>nts-worker deletions reapply --since &lt;iso8601&gt; [--file &lt;path&gt;]</c> (against the restored instance).
/// </summary>
/// <remarks>
/// Exit codes follow 11.6.1-T1: <c>0</c> done, <c>1</c> a check failed (a handler failed or is missing, the file is
/// unparseable, or a non-empty file applied nothing — never exit 0 having done nothing), <c>2</c> usage or
/// environment (bad arguments, the scratch-host guard <c>DataDeletion:AllowRestoreReapply</c> not set, the database
/// unreachable). The file and every message carry ids, dates and codes only; no secret is read from argv.
/// </remarks>
public static class DeletionsCommand
{
    /// <summary>The verb an operator types.</summary>
    public const string Verb = "deletions";

    public const int Success = 0;
    public const int CheckFailed = 1;
    public const int UsageOrEnvironment = 2;

    /// <summary>Usage line, printed on stderr for a malformed invocation.</summary>
    public const string UsageLine =
        "usage: nts-worker deletions export --since <iso8601> --out <path> | nts-worker deletions reapply --since <iso8601> [--file <path>]";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Runs the verb; <paramref name="args"/>[0] is <c>deletions</c>.</summary>
    public static async Task<int> RunAsync(
        string[] args,
        IServiceProvider services,
        CancellationToken cancellationToken,
        TextReader? input = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);

        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        if (args.Length < 2 || !TryOptions(args, out var options)
            || !options.TryGetValue("--since", out var sinceText)
            || !DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since))
        {
            return Fail(stderr, UsageLine, UsageOrEnvironment);
        }

        try
        {
            return args[1] switch
            {
                "export" when options.TryGetValue("--out", out var path) =>
                    await ExportAsync(services, since, path, stdout, cancellationToken).ConfigureAwait(false),
                "reapply" =>
                    await ReapplyAsync(services, since, options.GetValueOrDefault("--file"), input ?? Console.In, stdout, stderr, cancellationToken).ConfigureAwait(false),
                _ => Fail(stderr, UsageLine, UsageOrEnvironment),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a provider message can carry the connection string (09 README BD16).
            return Fail(stderr, $"nts-worker deletions failed: {exception.GetType().Name}", UsageOrEnvironment);
        }
    }

    private static async Task<int> ExportAsync(
        IServiceProvider services,
        DateTimeOffset since,
        string path,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IDeletionRestore>()
            .ExportAsync(since, cancellationToken).ConfigureAwait(false);

        await File.WriteAllLinesAsync(path, rows.Select(row => JsonSerializer.Serialize(Line.From(row), Json)), cancellationToken)
            .ConfigureAwait(false);

        stdout.WriteLine($"exported {rows.Count} deletion request(s)");

        return Success;
    }

    private static async Task<int> ReapplyAsync(
        IServiceProvider services,
        DateTimeOffset since,
        string? file,
        TextReader input,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();

        if (!scope.ServiceProvider.GetRequiredService<DataDeletionOptions>().AllowRestoreReapply)
        {
            return Fail(stderr, "nts-worker deletions reapply refused: this host is not a declared scratch instance (DataDeletion:AllowRestoreReapply).", UsageOrEnvironment);
        }

        var text = file is null
            ? await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
            : await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);

        var rows = new List<DeletionExportRow>();
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<Line>(raw, Json);
                if (parsed is null || parsed.Id == Guid.Empty || parsed.UserId == Guid.Empty
                    || parsed.Scope is null || parsed.Reason is null)
                {
                    throw new JsonException("incomplete line");
                }

                rows.Add(parsed.ToRow());
            }
            catch (JsonException)
            {
                return Fail(stderr, $"nts-worker deletions reapply: line {lineNumber} is not a deletion request.", CheckFailed);
            }
        }

        // Rows completed at or before the restore point were never lost.
        var work = rows.Where(row => row.CompletedAt is null || row.CompletedAt > since).ToList();

        var report = await scope.ServiceProvider.GetRequiredService<IDeletionRestore>()
            .ReapplyAsync(work, cancellationToken).ConfigureAwait(false);

        foreach (var problem in report.Problems)
        {
            stderr.WriteLine(problem);
        }

        stdout.WriteLine($"reapplied applied={report.Applied} failed={report.Failed}");

        if (report.Failed > 0 || (rows.Count > 0 && report.Applied == 0))
        {
            if (report.Failed == 0)
            {
                stderr.WriteLine("nts-worker deletions reapply: a non-empty file applied nothing.");
            }

            return CheckFailed;
        }

        return Success;
    }

    private static bool TryOptions(string[] args, out Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 2; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                return false;
            }

            options[args[index]] = args[index + 1];
        }

        return true;
    }

    private static int Fail(TextWriter stderr, string message, int code)
    {
        stderr.WriteLine(message);

        return code;
    }

    /// <summary>One NDJSON line: ids, wire names and dates only.</summary>
    private sealed record Line(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("user_id")] Guid UserId,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("target_id")] Guid? TargetId,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt,
        [property: JsonPropertyName("due_at")] DateTimeOffset DueAt,
        [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt)
    {
        public static Line From(DeletionExportRow row) =>
            new(row.Id, row.UserId, row.Scope, row.TargetId, row.Reason, row.RequestedAt, row.DueAt, row.CompletedAt);

        public DeletionExportRow ToRow() =>
            new(Id, UserId, Scope!, TargetId, Reason!, RequestedAt, DueAt, CompletedAt);
    }
}
