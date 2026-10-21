using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Prompts;
using NightTales.Infrastructure.Prompts;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Research;
using NightTales.TestKit.Stories;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// The true-crime attestation end to end with the real gate (02.3.3-T1, 02 README D8/D13): a passing
/// approval writes the <c>episode.true_crime_attested</c> row with ids only, and a refused one writes none.
/// </summary>
public sealed partial class ApproveStoryEndpointTests
{
    [Fact]
    public async Task ApproveStory_TrueCrime_WritesAttestedAuditRowWithVersionIdOnly()
    {
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var sheet = await SeedTrueCrimeApprovalAsync(world);
        using var factory = RealGateFactory(sheet);
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":true,"textVersion":"ta.v1"}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        AssertApproved(world);
        AttestationCount(world).ShouldBe(1);
        AuditDataKeys(world).ShouldBe("story_version_id,text_version", "D13: the row holds ids and versions only");
        AuditDataValues(world).ShouldBe($"{world.VersionId}|ta.v1");
    }

    [Fact]
    public async Task ApproveStory_TrueCrime_DoesNotWriteAttestedRowWhenGateFails()
    {
        // The refusal happens at the last sub-check, after the sheet, the sources and the similarity pass.
        var world = await SeedAsync(topic: Topic.TrueCrime);
        var sheet = await SeedTrueCrimeApprovalAsync(world);
        using var factory = RealGateFactory(sheet);
        using var client = factory.ClientFor(world.UserId);

        using var response = await ApproveAsync(client, world, """{"attestation":{"accepted":false,"textVersion":"ta.v1"}}""");

        await AssertProblemAsync(response, 400, "story_attestation_required");
        AttestationCount(world).ShouldBe(0);
        SqlProbe.Count(Database,
                $"SELECT count(*) FROM audit_logs WHERE action='episode.true_crime_attested' AND subject_user_id='{world.UserId}'")
            .ShouldBe(0, "a refused approval writes no attestation row");
        AssertNothingApproved(world);
    }

    /// <summary>
    /// The host's real gate (02.3.3-T1, sub-checks 3a-3e). The shared factory injects a recording fake by
    /// default, so a test that must exercise the production implementation re-registers it behind its real
    /// dependencies (read model, similarity settings, bound options) and, when given one, the fact-sheet guard.
    /// A test that configures <c>Story:TrueCrime:AttestationVersion</c> away from the shipped marker must
    /// pass it as <paramref name="attestationVersion"/> too: the host refuses to start when the version the
    /// catalog ships for any language is not the configured one (S1), so the catalog has to serve it.
    /// </summary>
    private ApiFactory RealGateFactory(ApprovedFactSheet? approvedSheet = null, string? attestationVersion = null) =>
        Factory().WithServices(services =>
        {
            services.RemoveAll<IStoryApprovalGate>();
            services.AddScoped<IStoryApprovalGate, StoryApprovalGate>();
            if (attestationVersion is { } version)
            {
                services.RemoveAll<IPromptCatalog>();
                services.AddSingleton<IPromptCatalog>(provider => new RelabelledCatalog(
                    new PromptCatalog(provider.GetRequiredService<IPromptResourceSource>()),
                    version));
            }

            if (approvedSheet is { } sheet)
            {
                services.RemoveAll<IFactSheetGuard>();
                services.AddSingleton<IFactSheetGuard>(new FakeFactSheetGuard { ApprovedSheet = sheet });
            }
        });

    /// <summary>
    /// A real approved sheet for the episode, two distinct fetched url sources that kept facts cite (F13) and
    /// the version pinned to that sheet with a passing similarity score, so only the attestation is left open.
    /// </summary>
    private async Task<ApprovedFactSheet> SeedTrueCrimeApprovalAsync(World world)
    {
        var sheet = await new FactSheetBuilder(Database, new TestCurrentUser { Id = world.UserId })
            .CreateAsync(world.UserId, world.EpisodeId, Ct);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId }))
        {
            db.StorySources.Add(Fetched(world, first, new string('a', 64)));
            db.StorySources.Add(Fetched(world, second, new string('b', 64)));
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database,
            $"UPDATE story_versions SET fact_sheet_id = '{sheet.Id}', similarity_score = 0 WHERE id = '{world.VersionId}'");

        return new ApprovedFactSheet(
            sheet.Id,
            sheet.Version,
            [new FactForWriter("f1", "Fact one", ["s1"]), new FactForWriter("f2", "Fact two", ["s2"])],
            new Dictionary<string, Guid>(StringComparer.Ordinal) { ["s1"] = first, ["s2"] = second });
    }

    private static StorySource Fetched(World world, Guid id, string sha256)
    {
        var url = SourceUrl.Parse($"https://news.example.com/{id:N}", new FakePublicSuffixList()).Value;
        var source = StorySource.CreateUrl(id, world.UserId, world.EpisodeId, url, Start).Value;
        source.MarkFetched(url.Value, "Article", "text/html", sha256, $"research/{id:N}", 4_000, Start);

        return source;
    }

    private string AuditDataKeys(World world) => SqlProbe.ScalarText(Database,
        $"SELECT (SELECT string_agg(k, ',' ORDER BY k) FROM jsonb_object_keys(data) AS k) FROM audit_logs WHERE action='episode.true_crime_attested' AND subject_user_id='{world.UserId}'");

    private string AuditDataValues(World world) => SqlProbe.ScalarText(Database,
        $"SELECT data->>'story_version_id' || '|' || (data->>'text_version') FROM audit_logs WHERE action='episode.true_crime_attested' AND subject_user_id='{world.UserId}'");

    /// <summary>
    /// The shipped catalog with its attestation clause relabelled to <paramref name="version"/> (02.3.3-T1
    /// S1): the file bytes and every other lookup stay the released ones, so only the version the start-up
    /// check and the read model compare is moved. Without it, configuring another version would fail the boot.
    /// </summary>
    /// <param name="inner">The real catalog over the host's embedded resource source.</param>
    /// <param name="version">The version to report for every language.</param>
    private sealed class RelabelledCatalog(IPromptCatalog inner, string version) : IPromptCatalog
    {
        public string Release => inner.Release;

        public IReadOnlyDictionary<string, PromptFileInfo> Files => inner.Files;

        public string GetSystemPrompt() => inner.GetSystemPrompt();

        public string GetPreset(Topic topic, Language language) => inner.GetPreset(topic, language);

        public string GetShared(PromptFile file, Language language) => inner.GetShared(file, language);

        public string GetStepPrompt(PromptStep promptStep) => inner.GetStepPrompt(promptStep);

        public AttestationText GetAttestation(Language language) =>
            new(version, inner.GetAttestation(language).Text);

        public ReadOnlyMemory<byte> GetSchema(string name) => inner.GetSchema(name);

        public string BuildPromptVersion(PromptVersionParts parts) => inner.BuildPromptVersion(parts);
    }
}
