using NightTales.Domain.Research;
using NightTales.Infrastructure.Research.Http;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

public sealed class CountingStreamTests
{
    [Fact]
    public async Task UnderLimit_PassesThrough()
    {
        var source = new byte[100];
        new Random(1).NextBytes(source);
        await using var stream = new CountingStream(new MemoryStream(source), limit: 100);
        using var copy = new MemoryStream();

        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);

        copy.ToArray().ShouldBe(source);
        stream.BytesRead.ShouldBe(100);
    }

    [Fact]
    public async Task ExceedsLimit_Throws()
    {
        await using var stream = new CountingStream(new MemoryStream(new byte[101]), limit: 100);

        var thrown = await Should.ThrowAsync<SourceFetchException>(
            () => stream.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));

        thrown.Error.Code.ShouldBe(SourceErrors.SourceTooLargeCode);
    }

    [Fact]
    public void ExceedsLimit_SynchronousReadThrows()
    {
        using var stream = new CountingStream(new MemoryStream(new byte[10]), limit: 4);

        Should.Throw<SourceFetchException>(() => stream.ReadExactly(new byte[10]));
    }
}
