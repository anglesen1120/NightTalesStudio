using System.Text;
using ErrorOr;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Storage;
using NightTales.TestKit.Storage;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Storage;

/// <summary><see cref="ExtractedTextStore"/> over the in-memory blob store (02.5.2-T2, 02 D10, plan §5.3).</summary>
public sealed class ExtractedTextStoreTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EpisodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SourceId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly InMemoryBlobStore _blobs = new();
    private readonly StorageSettings _settings = new();

    private ExtractedTextStore Store => new(_blobs, _settings);

    private static string Sha(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private Task<ErrorOr<string>> WriteAsync(string text, string? sha = null, Guid? source = null) =>
        Store.WriteAsync(UserId, EpisodeId, source ?? SourceId, sha ?? Sha(text), text, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Write_ReturnsContentAddressedKey_InMediaBucket()
    {
        var sha = Sha("hello");

        var key = await WriteAsync("hello");

        key.IsError.ShouldBeFalse();
        key.Value.ShouldBe($"research/{UserId:D}/{EpisodeId:D}/{SourceId:D}/{sha}.txt");
        key.Value.ShouldBe(ExtractedTextStore.KeyFor(UserId, EpisodeId, SourceId, sha));
        _blobs.Contains(_settings.Buckets.Media, key.Value).ShouldBeTrue();
    }

    [Fact]
    public async Task Write_StoresPlainTextUtf8ContentType()
    {
        var key = (await WriteAsync("hello")).Value;

        var opened = await _blobs.OpenReadAsync(_settings.Buckets.Media, key, TestContext.Current.CancellationToken);

        opened.IsError.ShouldBeFalse();
        opened.Value.ContentType.ShouldBe("text/plain; charset=utf-8");
        ExtractedTextStore.ContentType.ShouldBe("text/plain; charset=utf-8");
        await opened.Value.Content.DisposeAsync();
    }

    [Fact]
    public async Task Write_Then_Read_RoundTripsExactly()
    {
        const string text = "Xin chào thế giới \ud83d\ude00\r\nline\ttab\n\n";

        var key = (await WriteAsync(text)).Value;
        var read = await Store.ReadAsync(key, TestContext.Current.CancellationToken);

        read.IsError.ShouldBeFalse();
        read.Value.ShouldBe(text);
    }

    [Fact]
    public async Task Write_Then_Read_KeepsALeadingZeroWidthNoBreakSpace()
    {
        const string text = "\ufeffstarts with a BOM character";

        var key = (await WriteAsync(text)).Value;
        var read = await Store.ReadAsync(key, TestContext.Current.CancellationToken);

        read.IsError.ShouldBeFalse();
        read.Value.ShouldBe(text);
    }

    [Fact]
    public async Task Write_WritesNoByteOrderMark()
    {
        var key = (await WriteAsync("abc")).Value;

        var opened = await _blobs.OpenReadAsync(_settings.Buckets.Media, key, TestContext.Current.CancellationToken);
        using var memory = new MemoryStream();
        await opened.Value.Content.CopyToAsync(memory, TestContext.Current.CancellationToken);
        await opened.Value.Content.DisposeAsync();

        memory.ToArray().ShouldBe(Encoding.UTF8.GetBytes("abc"));
    }

    [Fact]
    public async Task Write_SameText_SameKey_SingleObject()
    {
        var first = await WriteAsync("same");
        var second = await WriteAsync("same");

        second.Value.ShouldBe(first.Value);
        _blobs.CountOf(BlobOperation.Put).ShouldBe(2);
        (await _blobs.ListKeysAsync(_settings.Buckets.Media, "research/", TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Write_ChangedText_WritesNewKey()
    {
        var first = await WriteAsync("one");
        var second = await WriteAsync("two");

        second.Value.ShouldNotBe(first.Value);
    }

    [Fact]
    public async Task Write_DifferentSource_DifferentKey()
    {
        var first = await WriteAsync("one");
        var second = await WriteAsync("one", source: Guid.NewGuid());

        second.Value.ShouldNotBe(first.Value);
    }

    [Fact]
    public async Task Write_HashMismatch_ReturnsServiceUnavailable()
    {
        var result = await WriteAsync("text", sha: Sha("other"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task Write_BlobStoreFailure_IsPropagated()
    {
        _blobs.AsyncFailure = Error.Failure(ErrorCodes.ServiceUnavailable, "down");

        var result = await WriteAsync("text");

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task Write_LoneSurrogate_Throws_NothingStored() =>
        await Should.ThrowAsync<EncoderFallbackException>(() => WriteAsync("bad \ud800 text"));

    [Fact]
    public async Task Write_BlankHash_Throws() =>
        await Should.ThrowAsync<ArgumentException>(() =>
            Store.WriteAsync(UserId, EpisodeId, SourceId, " ", "t", TestContext.Current.CancellationToken));

    [Fact]
    public async Task Read_MissingKey_ReturnsNotFound()
    {
        var result = await Store.ReadAsync("research/none.txt", TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task Read_InvalidUtf8Object_Throws()
    {
        _blobs.Seed(_settings.Buckets.Media, "research/bad.txt", [0xc3, 0x28], "text/plain; charset=utf-8");

        await Should.ThrowAsync<DecoderFallbackException>(() =>
            Store.ReadAsync("research/bad.txt", TestContext.Current.CancellationToken));
    }
}
