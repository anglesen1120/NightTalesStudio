using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Identity;

/// <summary>
/// The two breached-password sources of <c>Security:BreachedPasswords:Mode</c> (09.3.1-T1 Tests table).
/// The range cases use a hand-written <see cref="HttpMessageHandler"/> that records what actually left
/// the process — no mocking library (09 README D13).
/// </summary>
public sealed class BreachedPasswordCheckerTests
{
    private const string Password = "correct-horse-battery-staple";

    [Fact]
    public async Task RangeMode_SendsOnlyFirstFiveHexChars()
    {
        var ct = TestContext.Current.CancellationToken;
        var expectedHash = Sha1Hex(Password);
        var suffix = expectedHash[5..];

        var handler = new RecordingRangeHandler(_ => $"{suffix}:42\r\n{new string('0', 35)}:7\r\n");
        using var checker = Checker(handler);

        var result = await checker.IsBreachedAsync(Password, ct);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeTrue("the response lists this password's suffix");

        var path = handler.RequestPath;
        path.ShouldNotBeNull();
        var requested = path!;
        requested.ShouldBe($"/range/{expectedHash[..5]}");
        requested.Length.ShouldBe("/range/".Length + 5, "only the 5-character prefix may leave the server");
        requested.ShouldNotContain(suffix, Case.Sensitive, "the rest of the hash is matched locally");
    }

    [Fact]
    public async Task RangeMode_ValidNoMatchBody_IsNotBreached()
    {
        var ct = TestContext.Current.CancellationToken;

        // A well-formed body that does not list this password's suffix is a real "not breached".
        var other = new string('0', 35);
        using var checker = Checker(new RecordingRangeHandler(_ => $"{other}:7\r\n"));

        var result = await checker.IsBreachedAsync(Password, ct);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>maintenance</body></html>")]
    [InlineData("ABC:1\r\n")]
    [InlineData("0000000000000000000000000000000000:notanumber\r\n")]
    [InlineData("0000000000000000000000000000000000AA:7\r\n")]
    public async Task RangeMode_UnusableBody_FailsClosed(string body)
    {
        var ct = TestContext.Current.CancellationToken;
        using var checker = Checker(new RecordingRangeHandler(_ => body));

        var result = await checker.IsBreachedAsync(Password, ct);

        result.IsError.ShouldBeTrue($"'{body}' is not a SUFFIX:COUNT body, so it must fail closed, not read as 'not breached'");
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task RangeMode_TruncatedBody_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;

        // One good line and then garbage: a partially read answer is not trustworthy either.
        using var checker = Checker(
            new RecordingRangeHandler(_ => $"{new string('0', 35)}:7\r\n<htm"));

        var result = await checker.IsBreachedAsync(Password, ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task RangeMode_ProviderUnreachable_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var checker = Checker(new ThrowingRangeHandler());

        var result = await checker.IsBreachedAsync(Password, ct);

        result.IsError.ShouldBeTrue("an unreachable provider must never accept a possibly-breached password");
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task RangeMode_ProviderErrorStatus_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var checker = Checker(new RecordingRangeHandler(_ => "when there is no answer", HttpStatusCode.InternalServerError));

        var result = await checker.IsBreachedAsync(Password, ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task RangeMode_ProviderTimeout_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;

        // The provider never answers and the client's own budget trips first: a timeout is the same
        // fail-closed answer as a transport failure (09.3.1-T1 failure handling, "2 s budget").
        using var checker = Checker(new HangingRangeHandler(), TimeSpan.FromMilliseconds(50));

        var result = await checker.IsBreachedAsync(Password, ct);

        result.IsError.ShouldBeTrue("a provider that cannot answer inside its budget never accepts the password");
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task ListMode_MatchesBundledTopList()
    {
        var ct = TestContext.Current.CancellationToken;
        var checker = new TopListPasswordChecker(
            Options.Create(new BreachedPasswordOptions { Mode = BreachedPasswordMode.List, TopListSize = 100_000 }));

        // The bundled placeholder list is the SHA-1s of "nts-blocked-placeholder-<index:06d>".
        var known = await checker.IsBreachedAsync("nts-blocked-placeholder-000042", ct);
        var unknown = await checker.IsBreachedAsync("ApplesOranges42", ct);

        known.IsError.ShouldBeFalse();
        known.Value.ShouldBeTrue("the entry is in the bundled list");
        unknown.Value.ShouldBeFalse();
    }

    [Fact]
    public async Task RangeMode_CallerCancellation_Propagates_NotMappedToUnavailable()
    {
        // A caller-cancelled request must surface as cancellation, never as 503 service_unavailable:
        // only a provider failure fails closed.
        using var checker = Checker(new HangingRangeHandler());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await checker.IsBreachedAsync(Password, cancelled.Token));
    }

    [Fact]
    public void ListMode_NonPositiveTopListSize_Throws()
    {
        // The options validator refuses TopListSize <= 0 at start-up; the checker refuses it too, so a
        // hand-built instance can never mean "honour no entry of the list".
        Should.Throw<ArgumentOutOfRangeException>(
            () => new TopListPasswordChecker(
                Options.Create(new BreachedPasswordOptions { Mode = BreachedPasswordMode.List, TopListSize = 0 })));
    }

    [Fact]
    public void ListMode_ResourceIsBundled()
    {
        TopListPasswordChecker.HasUsableBundledList().ShouldBeTrue(
            "mode list fails at start-up without a usable bundled resource; it must ship with the assembly");
    }

    [Fact]
    public void ListMode_EmptyList_FailsToLoad()
    {
        using var empty = new MemoryStream();

        Should.Throw<InvalidOperationException>(() => TopListPasswordChecker.Load(empty, 100_000));
    }

    [Fact]
    public void ListMode_CommentOnlyList_FailsToLoad()
    {
        // A truncated or documentation-only file must not degrade to "matches nothing".
        using var comments = new MemoryStream(Encoding.UTF8.GetBytes("# provenance only\n\n   \n"));

        Should.Throw<InvalidOperationException>(() => TopListPasswordChecker.Load(comments, 100_000));
    }

    [Fact]
    public void ListMode_ListWithOneEntry_Loads()
    {
        using var single = new MemoryStream(Encoding.UTF8.GetBytes("# header\nb2\nA1\n"));

        var hashes = TopListPasswordChecker.Load(single, 100_000);

        hashes.ShouldBe(["A1", "B2"], "entries are upper-cased and sorted for the binary search");
    }

    private static string Sha1Hex(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);

#pragma warning disable CA5350 // The provider's protocol is SHA-1; the test recomputes the same key independently.
        var digest = SHA1.HashData(bytes);
#pragma warning restore CA5350

        return Convert.ToHexString(digest);
    }

    private static KAnonymityRangeChecker Checker(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.pwnedpasswords.test/") };

        if (timeout is { } budget)
        {
            http.Timeout = budget;
        }

        return new KAnonymityRangeChecker(http);
    }

    /// <summary>Records the requested path and answers from <paramref name="body"/>.</summary>
    private sealed class RecordingRangeHandler(
        Func<HttpRequestMessage, string> body,
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal string? RequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body(request), Encoding.UTF8, "text/plain"),
            });
        }
    }

    private sealed class ThrowingRangeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("the provider is unreachable");
    }

    /// <summary>Never answers; it honours the request token so the client's own budget is what trips.</summary>
    private sealed class HangingRangeHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
