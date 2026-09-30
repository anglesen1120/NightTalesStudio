using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Catalog;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Upserts the <c>style_templates</c> catalog (09.2.5 plan §3.1/§3.4) by natural key
/// <see cref="StyleTemplate.Name"/>, from <c>Seed:StyleTemplatesFile</c> in every environment (03 CR-1).
/// Rows absent from the file are inserted, present rows are updated only in the catalog-owned fields,
/// and nothing is ever deleted.
/// </summary>
/// <remarks>
/// 03.1.1-T2 will wrap the row validation in <c>StyleTemplateRules</c> and check the file's
/// <c>negativeClauseVersion</c> against <c>NegativeClause.Version</c>; this task validates only the
/// <c>{term}</c> placeholder, the topic enum and duplicate names.
/// </remarks>
public sealed partial class StyleTemplateSeeder(
    IAppDbContext db,
    IOptions<SeedOptions> options,
    IHostEnvironment environment,
    ILogger<StyleTemplateSeeder> logger) : ISeeder
{
    /// <inheritdoc />
    public string Name => "style_templates";

    /// <inheritdoc />
    public int Order => 20;

    /// <inheritdoc />
    public async ValueTask<SeedReport> SeedAsync(CancellationToken ct)
    {
        var rows = LoadRows();
        if (rows is null)
        {
            return new SeedReport(Name, 0, 0, 0);
        }

        try
        {
            return await ApplyAsync(rows, ct);
        }
        catch (DbUpdateException exception) when (SeedDbContext.IsUniqueViolation(exception))
        {
            LogConcurrentInsertRetry();
            SeedDbContext.ResetTracking(db);

            return await ApplyAsync(rows, ct);
        }
    }

    private IReadOnlyList<StyleTemplateSeedRow>? LoadRows()
    {
        var path = SeedFiles.Resolve(options.Value.StyleTemplatesFile, environment);
        if (path is null)
        {
            if (!environment.IsDevelopment())
            {
                throw new SeedValidationException($"seed_style_templates_missing:{options.Value.StyleTemplatesFile}");
            }

            LogCatalogMissing(options.Value.StyleTemplatesFile);

            return null;
        }

        return SeedCatalogFile.ReadStyleTemplates(File.ReadAllText(path));
    }

    private async ValueTask<SeedReport> ApplyAsync(IReadOnlyList<StyleTemplateSeedRow> rows, CancellationToken ct)
    {
        var existing = await db.StyleTemplates.ToDictionaryAsync(template => template.Name, ct);
        var inserted = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Name, out var template))
            {
                db.StyleTemplates.Add(StyleTemplate.Create(
                    row.Name,
                    row.Template,
                    row.TopicHint,
                    row.PreviewStorageKey,
                    row.Active));
                inserted++;
                continue;
            }

            if (template.Template == row.Template
                && template.TopicHint == row.TopicHint
                && template.PreviewStorageKey == row.PreviewStorageKey
                && template.Active == row.Active)
            {
                skipped++;
                continue;
            }

            template.UpdateFromCatalog(row.Template, row.TopicHint, row.PreviewStorageKey, row.Active);
            updated++;
        }

        if (inserted + updated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new SeedReport(Name, inserted, updated, skipped);
    }

    [LoggerMessage(EventId = 2310, Level = LogLevel.Warning, Message = "seed.style-templates.catalog-missing path={Path}")]
    private partial void LogCatalogMissing(string path);

    [LoggerMessage(EventId = 2311, Level = LogLevel.Information, Message = "seed.style-templates.concurrent-insert-retry")]
    private partial void LogConcurrentInsertRetry();
}
