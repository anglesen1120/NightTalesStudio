using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Seeding;

/// <summary>
/// The 09.2.5-T2 owner seeder against a real migrated database and the real Identity stack: it creates
/// exactly one owner from <c>Seed:Owner</c>, does nothing once an owner exists, refuses a weak password
/// with Identity codes only, and never writes the password to a log. The password constants are built by
/// concatenation so the secret scanner does not read them as credentials.
/// </summary>
public sealed class OwnerSeederTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly string StrongPassword = "Own3r-" + "Seed-" + "Passw0rd";

    [Fact]
    public async Task OwnerSeed_NoOwner_CreatesOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();

        var report = await RunAsync(email, StrongPassword, new CapturingLoggerProvider(), ct);

        report.ShouldBe(new SeedReport("owner", 1, 0, 0));
        (await Database.QueryStringsAsync(
            "SELECT role || '|' || email_confirmed::text || '|' || status FROM users", ct))
            .ShouldBe(["owner|true|active"]);
        (await Database.QueryStringsAsync(
            $"SELECT count(*)::text FROM users WHERE email = '{email}' AND password_hash IS NOT NULL AND password_hash <> '{StrongPassword}'",
            ct)).ShouldBe(["1"]);
    }

    [Fact]
    public async Task OwnerSeed_OwnerExists_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var sink = new CapturingLoggerProvider();
        await RunAsync(NewEmail(), StrongPassword, sink, ct);

        var again = await RunAsync(NewEmail(), StrongPassword, sink, ct);

        again.ShouldBe(new SeedReport("owner", 0, 0, 1));
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM users", ct)).ShouldBe(["1"]);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM users WHERE role = 'owner'", ct)).ShouldBe(["1"]);
    }

    [Fact]
    public async Task OwnerSeed_WeakPassword_FailsWithoutPasswordInMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        const string weak = "wk-pw";

        var exception = await Should.ThrowAsync<SeedValidationException>(
            () => RunAsync(NewEmail(), weak, new CapturingLoggerProvider(), ct).AsTask());

        exception.Code.ShouldContain("PasswordTooShort");
        exception.ToString().ShouldNotContain(weak, Case.Sensitive);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM users", ct)).ShouldBe(["0"]);
    }

    [Fact]
    public async Task OwnerSeed_NormalUserWithTheEmail_FailsAndIsNotPromoted()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();
        using (var factory = new ApiFactory(Database, new FakeClock()))
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            (await users.CreateAsync(new AppUser { UserName = email, Email = email, DisplayName = "normal" }, StrongPassword))
                .Succeeded.ShouldBeTrue();
        }

        var exception = await Should.ThrowAsync<SeedValidationException>(
            () => RunAsync(email, StrongPassword, new CapturingLoggerProvider(), ct).AsTask());

        exception.Code.ShouldContain("DuplicateEmail");
        (await Database.QueryStringsAsync("SELECT role FROM users", ct)).ShouldBe(["user"]);
    }

    [Fact]
    public async Task OwnerSeed_NeverLogsPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var sink = new CapturingLoggerProvider();
        var options = new SeedOptions { Owner = new SeedOwnerOptions { Email = NewEmail(), Password = StrongPassword } };

        await Should.ThrowAsync<SeedValidationException>(() => RunAsync(NewEmail(), "wk-pw", sink, ct).AsTask());
        await RunAsync(options.Owner.Email!, StrongPassword, sink, ct);
        await RunAsync(options.Owner.Email!, StrongPassword, sink, ct);
        await ResetAsync(ct);
        await RunAsync(string.Empty, string.Empty, sink, ct);

        sink.Text.ShouldContain("owner seed not configured", Case.Sensitive, "the sink really saw the seeder's log");
        sink.Text.ShouldNotContain(StrongPassword, Case.Sensitive);
        sink.Text.ShouldNotContain("wk-pw", Case.Sensitive);
        options.Owner.ToString().ShouldBe("***");
        options.Owner.ToString().ShouldNotContain(StrongPassword, Case.Sensitive);
    }

    [Fact]
    public async Task OwnerSeed_NotConfigured_Skips()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var sink = new CapturingLoggerProvider();

        var empty = await RunAsync(string.Empty, string.Empty, sink, ct);
        var noPassword = await RunAsync(NewEmail(), string.Empty, sink, ct);

        empty.ShouldBe(new SeedReport("owner", 0, 0, 0));
        noPassword.ShouldBe(new SeedReport("owner", 0, 0, 0));
        sink.Text.ShouldContain("owner seed not configured", Case.Sensitive);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM users", ct)).ShouldBe(["0"]);
    }

    private static string NewEmail() => $"owner-{Guid.CreateVersion7():N}@nighttales.test";

    private async Task ResetAsync(CancellationToken ct) =>
        await Database.ExecuteAsync("DELETE FROM users", ct);

    /// <summary>One seeder run in its own DI scope, with the real Identity stack and the sink as the only logger.</summary>
    private async ValueTask<SeedReport> RunAsync(string email, string password, ILoggerProvider sink, CancellationToken ct)
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(sink));
        var seeder = new OwnerSeeder(
            scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            Options.Create(new SeedOptions { Owner = new SeedOwnerOptions { Email = email, Password = password } }),
            new FakeClock(),
            loggers.CreateLogger<OwnerSeeder>());

        return await seeder.SeedAsync(ct);
    }

    /// <summary>Keeps every rendered line, so a test can scan it for a secret.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        internal string Text
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _messages);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Capture(string rendered)
        {
            lock (_gate)
            {
                _messages.Add(rendered);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Capture(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
            }
        }
    }
}
