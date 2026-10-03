using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Seeding;

namespace NightTales.Infrastructure.Persistence.Usage;

/// <summary>
/// The <c>price_versions</c> seeder of 02.2.4-T1 (02 README D14): when the row active now already carries the
/// config's hash it does nothing; otherwise it inserts one with <c>active_from = now</c> and
/// <c>version = max + 1</c>, under a transaction-scoped advisory lock so concurrent seeders never allocate the
/// same number. Rows are never updated, so reverting a config (A, B, A) inserts a third row with A's rates.
/// </summary>
/// <remarks>
/// It is an <see cref="ISeeder"/> like the catalog seeders of 09.2.5-T1, so the same two channels run it:
/// <see cref="SeedHostedService"/> in Development and the explicit <c>nts-seed</c> command elsewhere
/// (09.2.5 plan §7). It is deliberately <em>not</em> its own <c>IHostedService</c>: a hosted service's
/// <c>StartAsync</c> also runs when the build-time OpenAPI generator boots the Api (09.1.5-T1), where the
/// table does not exist yet, and the runner's own channel is the only start-up path a seeder may take.
/// </remarks>
/// <param name="db">The scoped tracked context <see cref="SeedRunner"/> shares with the other seeders.</param>
/// <param name="options">The <c>OpenAi:Pricing</c> section.</param>
/// <param name="clock">The <c>active_from</c> and <c>created_at</c> instant.</param>
public sealed class PriceVersionSeeder(
    IAppDbContext db,
    IOptions<PricingOptions> options,
    IClock clock) : ISeeder
{
    /// <inheritdoc />
    public string Name => "price_versions";

    /// <inheritdoc />
    public int Order => 40;

    /// <inheritdoc />
    public async ValueTask<SeedReport> SeedAsync(CancellationToken ct)
    {
        var (_, inserted) = await EnsureRowCoreAsync(ct).ConfigureAwait(false);

        return new SeedReport(Name, inserted ? 1 : 0, Updated: 0, inserted ? 0 : 1);
    }

    /// <summary>Inserts the version of the current config unless a row with its hash already exists.</summary>
    /// <param name="ct">Cancels the seed.</param>
    /// <returns>The row that now carries the config's hash.</returns>
    public async Task<PriceVersion> EnsureRowAsync(CancellationToken ct)
        => (await EnsureRowCoreAsync(ct).ConfigureAwait(false)).Row;

    private async Task<(PriceVersion Row, bool Inserted)> EnsureRowCoreAsync(CancellationToken ct)
    {
        // A bad config fails the seed: no row is better than a row that prices a call at 0 or negative.
        var problems = PricingOptions.Validate(options.Value);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{PricingOptions.SectionName}' is invalid: {string.Join("; ", problems)}");
        }

        var (ratesJson, limitsJson) = BuildDocuments(options.Value);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ratesJson + "\n" + limitsJson)));

        try
        {
            return await EnsureLockedAsync(ratesJson, limitsJson, hash, ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (SeedDbContext.IsUniqueViolation(exception))
        {
            // The advisory lock serialises every seeder, so this is a defensive second pass: drop the failed
            // insert from the tracker and decide again from what the other host committed.
            SeedDbContext.ResetTracking(db);

            return await EnsureLockedAsync(ratesJson, limitsJson, hash, ct).ConfigureAwait(false);
        }
    }

    private async Task<(PriceVersion Row, bool Inserted)> EnsureLockedAsync(
        string ratesJson,
        string limitsJson,
        string hash,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Serialises max(version) + 1 across the Api, the Worker and nts-seed: released at commit/rollback.
        await db.Database
            .ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('price_versions_seed'))", ct)
            .ConfigureAwait(false);

        var now = clock.UtcNow;

        // Compared with the ACTIVE row, not with any row of that hash: A -> B -> A must activate A's rates again.
        var active = await db.PriceVersions
            .AsNoTracking()
            .Where(row => row.ActiveFrom <= now)
            .OrderByDescending(row => row.ActiveFrom)
            .ThenByDescending(row => row.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (active is not null && string.Equals(active.ConfigHash, hash, StringComparison.Ordinal))
        {
            return (active, Inserted: false);
        }

        var next = (await db.PriceVersions.MaxAsync(row => (int?)row.Version, ct).ConfigureAwait(false) ?? 0) + 1;
        var created = PriceVersion.Create(next, now, ratesJson, limitsJson, hash, now);

        db.PriceVersions.Add(created);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return (created, Inserted: true);
    }

    private static (string RatesJson, string LimitsJson) BuildDocuments(PricingOptions config)
    {
        var models = config.Models.Count > 0 ? config.Models : new Dictionary<string, PricingOptions.ModelPricing>(PricingOptions.DefaultModels);

        var text = models.ToDictionary(
            pair => pair.Key,
            pair => new PriceRates(pair.Value.In, pair.Value.Cached, pair.Value.CacheWrite, pair.Value.Out),
            StringComparer.Ordinal);
        var image = config.Image.ToDictionary(
            pair => pair.Key,
            pair => new ImagePriceRate(pair.Value.Unit, pair.Value.PerImageUsd),
            StringComparer.Ordinal);

        return (
            new PriceVersionRates(text, image, config.WebSearchCallUsd, config.SurchargeThresholdTokens).ToJson(),
            PriceVersionJson.WriteLimits(PriceLimits.D4Defaults));
    }
}
