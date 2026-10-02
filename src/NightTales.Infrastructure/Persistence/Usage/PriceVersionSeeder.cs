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
/// The <c>price_versions</c> seeder of 02.2.4-T1 (02 README D14): when no row carries the config's hash it
/// inserts one with <c>active_from = now</c> and <c>version = max + 1</c>; when one does, it does nothing.
/// Rows are never updated.
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
        var (ratesJson, limitsJson) = BuildDocuments(options.Value);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ratesJson + "\n" + limitsJson)));

        var existing = await db.PriceVersions.SingleOrDefaultAsync(row => row.ConfigHash == hash, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return (existing, Inserted: false);
        }

        var next = (await db.PriceVersions.MaxAsync(row => (int?)row.Version, ct).ConfigureAwait(false) ?? 0) + 1;
        var now = clock.UtcNow;
        var created = PriceVersion.Create(next, now, ratesJson, limitsJson, hash, now);

        db.PriceVersions.Add(created);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The Api and the Worker can seed together: the loser of the unique (version / config_hash)
            // race finds the winner's row instead of failing the run.
            // Read untracked: the failed insert is still tracked by this (scope-owned) context, and the
            // winner may hold the same version number.
            var winner = await db.PriceVersions.AsNoTracking().SingleAsync(row => row.ConfigHash == hash, ct).ConfigureAwait(false);

            return (winner, Inserted: false);
        }

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
            new PriceVersionRates(text, image, config.WebSearchCallUsd).ToJson(),
            PriceVersionJson.WriteLimits(PriceLimits.D4Defaults));
    }
}
