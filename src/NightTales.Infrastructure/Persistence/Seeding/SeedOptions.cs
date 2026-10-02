namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// The <c>Seed</c> section (09.2.5 plan §3.3). It is bound in <c>AddSeeding</c> and validated on
/// start; the seeder content files are separate because the catalogs are owned by plan 04 (voices) and
/// plan 03 (style templates).
/// </summary>
public sealed class SeedOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Seed";

    /// <summary>
    /// Whether the Development auto-seed of <see cref="SeedHostedService"/> runs. It defaults to
    /// <c>true</c> and disables the auto-seed only when set to <c>false</c> explicitly (plan §7).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The dev catalog file (voices and feature flags), relative to the content root.</summary>
    public string CatalogFile { get; set; } = "seed/catalog.dev.json";

    /// <summary>The plan 03 style-template catalog, read in every environment (03 CR-1).</summary>
    public string StyleTemplatesFile { get; set; } = "seed/style-templates.v1.json";

    /// <summary>
    /// The plan 04 voice catalog, read in every environment (04 CR-9). It is the <em>same</em> file the
    /// proxy loads (<c>VOICE_CATALOG_FILE</c>, 04 D13), so the curated catalog and the backend rows
    /// cannot drift apart.
    /// </summary>
    public string VoiceCatalogFile { get; set; } = "seed/voices.v1.json";

    /// <summary>
    /// The plan 05 music catalog (05 README D8, §10 T85, 05.3.2-T3): auto-seeded in Development, seeded by
    /// <c>nts-seed</c> elsewhere. It is the file
    /// <c>worker-node/music/tools/check_catalog.py</c> proves, so the rules cannot drift apart.
    /// </summary>
    public string MusicTracksFile { get; set; } = "seed/music-tracks.v1.json";

    /// <summary>
    /// Whether <see cref="SeedRunner"/> continues after a failed seeder instead of stopping. The
    /// explicit <c>nts-seed</c> command keeps it <c>false</c>, so a failure is a non-zero exit.
    /// </summary>
    public bool ContinueOnError { get; set; }
}
