using Xunit;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// The token xUnit cancels the running test with (<c>xUnit1051</c>): every cancellable call in these
/// tests passes it explicitly, so a hung database call is cancelled instead of running until the test
/// timeout.
/// </summary>
internal static class TestContextToken
{
    internal static CancellationToken Value => TestContext.Current.CancellationToken;
}
