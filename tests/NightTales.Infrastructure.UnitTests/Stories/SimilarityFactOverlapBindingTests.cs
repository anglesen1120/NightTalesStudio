using System.Reflection;
using Microsoft.Extensions.Configuration;
using NightTales.Application.Features.Research.Facts;
using NightTales.Application.Features.Research.GenerateFactSheet;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// 02.5.6 owner decision Q2=C: the fact verbatim-copy threshold is <c>Similarity:FactOverlapBlock</c>, set to
/// the calibrated 0.05 in both host appsettings. These tests bind the real <c>Similarity</c> section of each
/// file, prove the key (not the 0.15 default) decides a fact copy, and document the parallel
/// <c>Research:FactSheet:CopiedFactThreshold</c> key. The pipeline's own <c>ValidateAsync</c> needs the
/// database, so the decision is exercised through the pure <see cref="FactSheetValidator"/> with the
/// production <c>ToScoreThreshold</c> conversion.
/// </summary>
public sealed class SimilarityFactOverlapBindingTests
{
    private const double CalibratedFactOverlapBlock = 0.05;
    private const string CalibratedCopiedFactThreshold = "0.05";
    private static readonly Guid Source1 = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static TheoryData<string> HostAppSettings => new()
    {
        "src/NightTales.Api/appsettings.json",
        "src/NightTales.Worker/appsettings.json",
    };

    [Theory]
    [MemberData(nameof(HostAppSettings))]
    public void FactOverlapBlock_BindsFromSimilaritySection_ToCalibratedValue(string relativePath)
    {
        var settings = BindSimilarity(LoadAppSettings(relativePath));

        settings.FactOverlapBlock.ShouldBe(CalibratedFactOverlapBlock, 1e-9);
    }

    [Theory]
    [MemberData(nameof(HostAppSettings))]
    public void CopiedFactThreshold_IsDocumentedAtCalibratedValue_AndIsNotTheBoundKey(string relativePath)
    {
        var configuration = LoadAppSettings(relativePath);

        configuration["Research:FactSheet:CopiedFactThreshold"].ShouldBe(CalibratedCopiedFactThreshold);
        BindSimilarity(configuration).FactOverlapBlock.ShouldBe(CalibratedFactOverlapBlock, 1e-9);
    }

    [Fact]
    public void FactOverlapBlock_IsReadFromTheSimilarityKey_NotFromTheResearchKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Similarity:FactOverlapBlock"] = "0.42",
                ["Research:FactSheet:CopiedFactThreshold"] = "0.9",
            })
            .Build();

        BindSimilarity(configuration).FactOverlapBlock.ShouldBe(0.42, 1e-9);
    }

    [Theory]
    [MemberData(nameof(HostAppSettings))]
    public void VerbatimCopy_ScoreAboveBoundThreshold_IsDropped_WhileDefaultWouldKeepIt(string relativePath)
    {
        var bound = BindSimilarity(LoadAppSettings(relativePath));
        var boundThreshold = ToScoreThreshold(bound.FactOverlapBlock);
        var defaultThreshold = ToScoreThreshold(SimilaritySettings.DefaultFactOverlapBlock);

        // 0.10 sits between the calibrated 0.05 and the 0.15 default: only the bound value drops it.
        var score = 0.10m;

        var bounded = FactSheetValidator.Validate(
            Output(),
            Context(boundThreshold),
            _ => score);
        var defaulted = FactSheetValidator.Validate(
            Output(),
            Context(defaultThreshold),
            _ => score);

        bounded.Facts.ShouldBeEmpty();
        bounded.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.VerbatimCopy);
        defaulted.Facts.ShouldHaveSingleItem();
        defaulted.Drops.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(HostAppSettings))]
    public void VerbatimCopy_ScoreExactlyAtBoundThreshold_IsKept(string relativePath)
    {
        var threshold = ToScoreThreshold(BindSimilarity(LoadAppSettings(relativePath)).FactOverlapBlock);

        var result = FactSheetValidator.Validate(Output(), Context(threshold), _ => threshold);

        result.Facts.ShouldHaveSingleItem();
        result.Drops.ShouldBeEmpty();
    }

    private static FactsV1 Output() => new([new FactDraftV1("A fact.", ["s1"], FactConfidence.High)]);

    private static FactSheetValidationContext Context(decimal threshold)
    {
        var aliases = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["s1"] = Source1 };
        var texts = new Dictionary<Guid, SourceText> { [Source1] = new SourceText(Source1, "source text 1") };

        return new FactSheetValidationContext(aliases, 0, threshold, 40, texts);
    }

    /// <summary>Calls the production <c>GenerateFactSheetPipeline.ToScoreThreshold</c>; fails loudly if it is renamed.</summary>
    private static decimal ToScoreThreshold(double threshold)
    {
        var method = typeof(GenerateFactSheetPipeline).GetMethod(
            "ToScoreThreshold",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull("GenerateFactSheetPipeline.ToScoreThreshold was renamed; update this test and its call site.");

        return (decimal)method.Invoke(null, [threshold])!;
    }

    private static IConfiguration LoadAppSettings(string relativePath) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), relativePath), optional: false)
            .Build();

    private static SimilaritySettings BindSimilarity(IConfiguration configuration)
    {
        var settings = new SimilaritySettings();
        configuration.GetSection("Similarity").Bind(settings);
        return settings;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("NightTales.sln was not found above the test output directory.");
        return directory.FullName;
    }
}
