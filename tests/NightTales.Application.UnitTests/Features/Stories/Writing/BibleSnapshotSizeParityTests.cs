using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Writing;

/// <summary>
/// S3-2 parity: <see cref="BibleSnapshotBuilder"/> measures the bible with its own copy of the serializer options.
/// The byte count it reports must equal the bytes the real assembler writes for the same bible in the writer data object.
/// </summary>
public sealed class BibleSnapshotSizeParityTests
{
    private static readonly LengthTarget ViStandard = new(1300, 2080, 20, 32, 6000, 2500, 6000);

    private static readonly AssemblerContext Context = new(Topic.Ghost, Language.Vi, "story/ghost.vi.v1.md", 6000);

    [Fact]
    public void BibleSnapshotBuilder_MeasuredBytes_EqualBibleBytesInWriterDataObject()
    {
        var content = new SeriesBibleContent(
            SeriesBibleContent.V1,
            new BibleNarrator("Bà Lan", "Người kể \"thật\" với giọng <chậm> & nhẹ", "Ngôi nhà hoang ở Hà Nội\nemoji \U0001F47B, U+2028 \u2028 end"),
            [new BibleCharacter("ch1", "Minh", "Khóc lúc nửa đêm 'nhỏ'", null)],
            [new BiblePlace("pl1", "Nhà thờ cũ", "Mái ngói\\ rêu phủ")],
            [new BibleRule("rl1", "Không tiết lộ kẻ phản diện sớm")],
            [new BibleEpisodeSummary(Guid.NewGuid(), "Tập 1: Tiếng khóc", "Tóm tắt có \"ngoặc kép\" và xuống dòng\nemoji \U0001F47B.", StoryStructureTags.Empty, DateTimeOffset.UnixEpoch)]);
        var limits = new BibleSnapshotLimits(50, 65536, 2000, 2000, 50, 100);
        var snapshot = BibleSnapshotBuilder.Build(content, 1, limits);
        var measured = BibleSnapshotBuilder.MeasureUtf8Bytes(snapshot);

        var prompt = new PromptAssembler(new FakeCatalog(), NullLogger<PromptAssembler>.Instance).Assemble(Request(snapshot), Context);

        using var doc = JsonDocument.Parse(prompt.UserDataJson);
        var raw = doc.RootElement.GetProperty("bible").GetRawText();
        Encoding.UTF8.GetByteCount(raw).ShouldBe(measured);
        measured.ShouldBeGreaterThan(raw.Length);
    }

    private static StoryWriteRequest Request(BibleSnapshot bible) =>
        new(
            new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new ReservationId(Guid.NewGuid()), GenerationTrigger.User, 1, "1"),
            new SeriesSnapshot(Guid.NewGuid(), Topic.Ghost, Language.Vi),
            new EpisodeSnapshot(Guid.NewGuid(), "A brief", LengthTier.Standard, null),
            ViStandard,
            bible,
            null,
            [],
            null);

    private sealed class FakeCatalog : IPromptCatalog
    {
        public string Release => "2026.10.1";

        public IReadOnlyDictionary<string, PromptFileInfo> Files { get; } = new Dictionary<string, PromptFileInfo>();

        public string GetSystemPrompt() => "SYSTEM";

        public string GetPreset(Topic topic, Language language) => "PRESET";

        public string GetShared(PromptFile file, Language language) => string.Empty;

        public string GetStepPrompt(PromptStep promptStep) => throw new NotSupportedException();

        public AttestationText GetAttestation(Language language) => new(string.Empty, string.Empty);

        public ReadOnlyMemory<byte> GetSchema(string name) => throw new NotSupportedException();

        public string BuildPromptVersion(PromptVersionParts parts) => $"version({parts.SystemKey}|{parts.PresetKey}|{parts.SchemaKey})";
    }
}
