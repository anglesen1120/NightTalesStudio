using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts real <c>series</c> rows (09 README D13, 09 W3 CR-41). A series needs its catalog
/// prerequisites (<c>voices</c>, <c>style_templates</c>), so the builder creates those first and reuses
/// them for every later series in the same database.
/// </summary>
/// <remarks>
/// The rows go through <see cref="AppDbContext"/> like production code, so the FKs, the enum wire values
/// and the audit stamps are exactly the ones a real request writes. 09.4.1-T1 adds the fields its
/// scenarios need.
/// </remarks>
public sealed class SeriesBuilder(TestDatabase database, TestCurrentUser currentUser, IClock? clock = null)
{
    /// <summary>The default voice id the builder seeds (a valid 04 D9 canonical name).</summary>
    public const string DefaultVoiceId = "nts-test-voice-vi";

    /// <summary>The default style template name the builder seeds.</summary>
    public const string DefaultTemplateName = "nts-test-template";

    /// <summary>Inserts one <c>voices</c> row, or returns the one already there.</summary>
    public async Task<Voice> EnsureVoiceAsync(
        string id = DefaultVoiceId,
        Language language = Language.Vi,
        VoiceGender gender = VoiceGender.Female,
        CancellationToken ct = default)
    {
        await using var context = database.CreateAppDbContext(currentUser, clock);

        var existing = await context.Voices.SingleOrDefaultAsync(voice => voice.Id == id, ct);
        if (existing is not null)
        {
            return existing;
        }

        var voice = Voice.Create(id, id, language, gender, ["narrator"]);
        context.Voices.Add(voice);
        await context.SaveChangesAsync(ct);

        return voice;
    }

    /// <summary>Inserts one <c>style_templates</c> row, or returns the one already there.</summary>
    public async Task<StyleTemplate> EnsureStyleTemplateAsync(
        string name = DefaultTemplateName,
        Topic? topicHint = null,
        CancellationToken ct = default)
    {
        await using var context = database.CreateAppDbContext(currentUser, clock);

        var existing = await context.StyleTemplates.SingleOrDefaultAsync(template => template.Name == name, ct);
        if (existing is not null)
        {
            return existing;
        }

        var template = StyleTemplate.Create(name, "Một câu chuyện về {term}.", topicHint);
        context.StyleTemplates.Add(template);
        await context.SaveChangesAsync(ct);

        return template;
    }

    /// <summary>
    /// Inserts one series for <paramref name="userId"/> and returns it. <paramref name="createdAt"/>
    /// defaults to the interceptor stamp, so a test that needs a specific ordering passes it.
    /// </summary>
    public async Task<Series> CreateAsync(
        Guid userId,
        string title = "Test series",
        Topic topic = Topic.Ghost,
        Language language = Language.Vi,
        Aspect aspect = Aspect.Landscape,
        string? voiceId = null,
        Guid? styleTemplateId = null,
        bool autopilotEnabled = false,
        string? autopilotSettings = null,
        DateTimeOffset? createdAt = null,
        CancellationToken ct = default)
    {
        var voice = voiceId ?? (await EnsureVoiceAsync(language: language, ct: ct)).Id;
        var template = styleTemplateId ?? (await EnsureStyleTemplateAsync(ct: ct)).Id;
        var id = Guid.CreateVersion7();

        await using var context = database.CreateAppDbContext(currentUser, clock);

        var series = ShellInstance.Create<Series>();
        context.Series.Add(series);

        ShellInstance.Set(context, series, nameof(Series.Id), id);
        ShellInstance.Set(context, series, nameof(Series.UserId), userId);
        ShellInstance.Set(context, series, nameof(Series.Title), title);
        ShellInstance.Set(context, series, nameof(Series.Topic), topic);
        ShellInstance.Set(context, series, nameof(Series.Language), language);
        ShellInstance.Set(context, series, nameof(Series.Aspect), aspect);
        ShellInstance.Set(context, series, nameof(Series.VoiceId), voice);
        ShellInstance.Set(context, series, nameof(Series.StyleTemplateId), template);
        ShellInstance.Set(context, series, nameof(Series.AutopilotEnabled), autopilotEnabled);
        ShellInstance.Set(context, series, nameof(Series.AutopilotSettings), autopilotSettings);

        if (createdAt is { } stamp)
        {
            ShellInstance.Set(context, series, nameof(Series.CreatedAt), stamp);
            ShellInstance.Set(context, series, nameof(Series.UpdatedAt), stamp);
        }

        await context.SaveChangesAsync(ct);

        return series;
    }
}
