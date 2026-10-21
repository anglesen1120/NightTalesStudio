using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Performance.Tests.Stories.Similarity;

/// <summary>End-to-end scoring latency for the maximum configured source corpus.</summary>
public sealed class SimilarityPerfTests
{
    private const int CorpusChars = 300_000;
    private const int MeasuredRuns = 20;

    [Fact]
    public void Score_StandardStoryAgainst10x300kChars_WithinBudget()
    {
        var random = new Random(256_001);
        var vocabulary = CreateVocabulary();
        vocabulary.Length.ShouldBe(3_000);
        var story = CreateStory(random, vocabulary);
        story.Segments.Sum(segment => segment.Text.Split(' ').Length).ShouldBe(1_000);
        var corpus = CreateCorpus(random, vocabulary, story);
        corpus.Length.ShouldBe(10);
        foreach (var source in corpus)
        {
            source.Text.Length.ShouldBe(CorpusChars);
        }

        var (budgetMs, configuredN, budgetSource) = ReadBudget();
        var settings = new SimilaritySettings();
        settings.N["vi"] = configuredN;
        var options = SimilarityOptions.For(Language.Vi, settings);
        options.N.ShouldBe(configuredN);
        SimilarityResult? result = null;
        for (var run = 0; run < 3; run++)
        {
            result = SimilarityScorer.Score(story, corpus, options);
        }

        var durations = new double[MeasuredRuns];
        for (var run = 0; run < MeasuredRuns; run++)
        {
            var started = Stopwatch.GetTimestamp();
            result = SimilarityScorer.Score(story, corpus, options);
            durations[run] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(durations);
        var p50 = durations[(int)Math.Ceiling(0.50 * MeasuredRuns) - 1];
        var p95 = durations[(int)Math.Ceiling(0.95 * MeasuredRuns) - 1];
        var line = FormattableString.Invariant(
            $"SIMILARITY_PERF p95_ms={p95:F3} p50_ms={p50:F3} max_ms={durations[^1]:F3} budget_ms={budgetMs} budget_source={budgetSource}");
        TestContext.Current.TestOutputHelper!.WriteLine(line);
        Console.WriteLine(line);

        result.ShouldNotBeNull();
        result.Score.ShouldBeGreaterThan(0d, "Copied paragraphs must exercise the matching path.");
        p95.ShouldBeLessThanOrEqualTo(budgetMs, line);
    }

    private static string[] CreateVocabulary()
    {
        // Synthetic syllables, not repeated numbered words: 1,500 Vietnamese + 1,500 English.
        string[] viOnsets = ["b", "đ", "kh", "ngh", "nh", "ph", "th", "tr", "ch", "v"];
        string[] viVowels = ["a", "ă", "â", "e", "ê", "i", "o", "ô", "ơ", "u"];
        string[] viCodas = ["", "m", "n", "ng", "nh", "p", "t", "c", "ch", "i", "y", "u", "o", "ên", "ươn"];
        string[] enOnsets = ["bl", "cl", "dr", "fl", "gr", "pl", "sl", "sp", "st", "sw"];
        string[] enVowels = ["a", "e", "i", "o", "u", "ai", "ee", "oa", "ou", "oo"];
        string[] enCodas = ["", "b", "d", "f", "g", "k", "l", "m", "n", "p", "r", "s", "t", "th", "sh"];
        var vocabulary = new List<string>(3_000);
        AddSyllables(vocabulary, viOnsets, viVowels, viCodas);
        AddSyllables(vocabulary, enOnsets, enVowels, enCodas);
        return vocabulary.ToArray();
    }

    private static void AddSyllables(List<string> vocabulary, string[] onsets, string[] vowels, string[] codas)
    {
        foreach (var onset in onsets)
        {
            foreach (var vowel in vowels)
            {
                foreach (var coda in codas)
                {
                    vocabulary.Add(onset + vowel + coda);
                }
            }
        }
    }

    private static StoryText CreateStory(Random random, string[] vocabulary)
    {
        var segments = new List<StorySegment>(13)
        {
            new("title", RandomWords(random, vocabulary, 12)),
            new("hook", RandomWords(random, vocabulary, 28)),
        };
        for (var paragraph = 0; paragraph < 10; paragraph++)
        {
            segments.Add(new StorySegment("p:" + paragraph.ToString(CultureInfo.InvariantCulture),
                RandomWords(random, vocabulary, 92)));
        }

        segments.Add(new StorySegment("ending", RandomWords(random, vocabulary, 40)));
        return new StoryText(segments);
    }

    private static string RandomWords(Random random, string[] vocabulary, int count)
    {
        var words = new string[count];
        for (var word = 0; word < count; word++)
        {
            words[word] = vocabulary[random.Next(vocabulary.Length)];
        }

        return string.Join(' ', words);
    }

    private static CorpusText[] CreateCorpus(Random random, string[] vocabulary, StoryText story)
    {
        var corpus = new CorpusText[10];
        for (var source = 0; source < corpus.Length; source++)
        {
            var text = new StringBuilder(CorpusChars + 32);
            FillTo(text, CorpusChars / 2, random, vocabulary);
            if (source == 0)
            {
                text.Append(story.Segments[2].Text).Append(' ');
                text.Append(story.Segments[3].Text).Append(' ');
            }

            FillTo(text, CorpusChars, random, vocabulary);
            var id = new Guid(source + 1, 0, 0, new byte[8]);
            corpus[source] = new CorpusText(id, text.ToString(0, CorpusChars));
        }

        return corpus;
    }

    private static void FillTo(StringBuilder text, int length, Random random, string[] vocabulary)
    {
        while (text.Length < length)
        {
            text.Append(vocabulary[random.Next(vocabulary.Length)]).Append(' ');
        }
    }

    private static (int BudgetMs, int ViN, string Source) ReadBudget()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent
                ?? throw new DirectoryNotFoundException("Cannot locate NightTales.sln for the performance budget.");
        }

        using var stream = File.OpenRead(Path.Combine(directory.FullName, "src", "NightTales.Api", "appsettings.json"));
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var defaults = new SimilaritySettings();
        if (!document.RootElement.TryGetProperty("Similarity", out var similarity))
        {
            return (defaults.Perf.BudgetMs, defaults.N["vi"], "default_missing_key");
        }

        // The budget is only meaningful at the n production runs with, so n comes from the same file (02.5.6-T2).
        var viN = similarity.TryGetProperty("N", out var nSection) && nSection.TryGetProperty("vi", out var vi)
            ? vi.GetInt32()
            : defaults.N["vi"];
        return similarity.TryGetProperty("Perf", out var perf) && perf.TryGetProperty("BudgetMs", out var budget)
            ? (budget.GetInt32(), viN, "appsettings")
            : (defaults.Perf.BudgetMs, viN, "default_missing_key");
    }
}
