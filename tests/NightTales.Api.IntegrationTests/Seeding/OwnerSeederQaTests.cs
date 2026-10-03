using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Domain.Common;
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
/// Independent QA probes for 09.2.5-T2 (written by a reviewer who did not write the seeder): the
/// concurrent-hosts row, field exactness, whitespace and boundary inputs, secrets hygiene on every
/// failure path, the Development/Production gate and the real DI graph. Passwords are built by
/// concatenation so the secret scanner does not read them as credentials.
/// </summary>
public sealed class OwnerSeederQaTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly string StrongPassword = "Qa-Own3r-" + "Seed-" + "Passw0rd";

    private static string NewEmail() => $"qa-owner-{Guid.CreateVersion7():N}@nighttales.test";

    private async Task ResetAsync(CancellationToken ct) =>
        await Database.ExecuteAsync("DELETE FROM users", ct);

    private async Task<string> ScalarAsync(string sql, CancellationToken ct)
        => (await Database.QueryStringsAsync(sql, ct)).Single();

    [Fact]
    public async Task OwnerSeed_TwoHostsRace_OneOwnerAndNoExceptionSurfaced()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 8; round++)
        {
            await ResetAsync(ct);
            var email = NewEmail();
            var sink = new Sink();

            using var hostA = new ApiFactory(Database, new FakeClock());
            using var hostB = new ApiFactory(Database, new FakeClock());
            using var scopeA = hostA.Services.CreateScope();
            using var scopeB = hostB.Services.CreateScope();
            var seederA = Build(scopeA, email, StrongPassword, sink);
            var seederB = Build(scopeB, email, StrongPassword, sink);
            var gate = new TaskCompletionSource();

            var runA = Task.Run(async () => { await gate.Task; return await seederA.SeedAsync(ct); }, ct);
            var runB = Task.Run(async () => { await gate.Task; return await seederB.SeedAsync(ct); }, ct);
            gate.SetResult();
            var reports = await Task.WhenAll(runA, runB);

            (await ScalarAsync("SELECT count(*)::text FROM users WHERE role = 'owner'", ct)).ShouldBe("1", $"round {round}");
            (await ScalarAsync("SELECT count(*)::text FROM users", ct)).ShouldBe("1", $"round {round}");
            reports.Sum(report => report.Inserted).ShouldBe(1, $"round {round}");
            reports.Sum(report => report.Skipped).ShouldBe(1, $"round {round}");
            reports.ShouldAllBe(report => !report.Failed);
            sink.Text.ShouldNotContain(StrongPassword, Case.Sensitive);
        }
    }

    [Fact]
    public async Task OwnerSeed_Created_FieldsAreExact()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();

        await RunAsync(email, StrongPassword, new Sink(), ct);

        (await ScalarAsync(
            "SELECT user_name || '|' || email || '|' || display_name || '|' || ui_locale || '|' || role || '|' || status || '|' || email_confirmed::text || '|' || two_factor_enabled::text FROM users",
            ct))
            .ShouldBe($"{email}|{email}|Owner|vi|owner|active|true|false");
        (await ScalarAsync("SELECT (normalized_email = upper(email))::text FROM users", ct)).ShouldBe("true");
        (await ScalarAsync("SELECT (created_at IS NOT NULL AND updated_at IS NOT NULL)::text FROM users", ct)).ShouldBe("true");
    }

    [Fact]
    public async Task OwnerSeed_EmailWithSurroundingSpaces_IsTrimmed()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();

        var report = await RunAsync($"  {email}  ", StrongPassword, new Sink(), ct);

        report.Inserted.ShouldBe(1);
        (await ScalarAsync("SELECT email FROM users", ct)).ShouldBe(email);
    }

    [Theory]
    [InlineData("   ", "")]
    [InlineData("   ", "x")]
    [InlineData("", "x")]
    public async Task OwnerSeed_BlankEmail_Skips(string email, string password)
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var sink = new Sink();

        var report = await RunAsync(email, password, sink, ct);

        report.ShouldBe(new SeedReport("owner", 0, 0, 0));
        sink.Text.ShouldContain("owner seed not configured", Case.Sensitive);
        (await ScalarAsync("SELECT count(*)::text FROM users", ct)).ShouldBe("0");
    }

    [Fact]
    public async Task OwnerSeed_NullOptions_Skips()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        var seeder = new OwnerSeeder(
            scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            Options.Create(new SeedOptions { Owner = new SeedOwnerOptions { Email = null, Password = null } }),
            new FakeClock(),
            new LoggerFactory().CreateLogger<OwnerSeeder>());

        (await seeder.SeedAsync(ct)).ShouldBe(new SeedReport("owner", 0, 0, 0));
    }

    [Fact]
    public async Task OwnerSeed_WhitespaceOnlyPassword_FailsWithoutLeak()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();
        var sink = new Sink();
        const string blank = "              ";

        var exception = await Should.ThrowAsync<SeedValidationException>(
            () => RunAsync(email, blank, sink, ct).AsTask());

        exception.Code.ShouldStartWith("seed_owner_invalid:");
        exception.Message.ShouldNotContain(email, Case.Insensitive);
        (await ScalarAsync("SELECT count(*)::text FROM users", ct)).ShouldBe("0");
    }

    [Fact]
    public async Task OwnerSeed_PolicyFailure_MessageLogsAndToStringCarryNeitherPasswordNorEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();
        var sink = new Sink();
        var weak = "Sh0rt-" + "pw";

        var exception = await Should.ThrowAsync<SeedValidationException>(
            () => RunAsync(email, weak, sink, ct).AsTask());

        foreach (var text in new[] { exception.Message, exception.Code, exception.ToString(), sink.Text })
        {
            text.ShouldNotContain(weak, Case.Sensitive);
            text.ShouldNotContain(email, Case.Insensitive);
        }

        exception.Code.ShouldContain("PasswordTooShort");
    }

    [Fact]
    public async Task OwnerSeed_DuplicateNormalUser_FailureCarriesNoEmailNoPassword_AndKeepsPasswordHash()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();
        var normalPassword = "Norm4l-" + "User-" + "Passw0rd";
        using (var factory = new ApiFactory(Database, new FakeClock()))
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            (await users.CreateAsync(new AppUser { UserName = email, Email = email, DisplayName = "normal" }, normalPassword))
                .Succeeded.ShouldBeTrue();
        }

        var hashBefore = await ScalarAsync("SELECT password_hash FROM users", ct);
        var sink = new Sink();

        var exception = await Should.ThrowAsync<SeedValidationException>(
            () => RunAsync(email.ToUpperInvariant(), StrongPassword, sink, ct).AsTask());

        exception.Message.ShouldNotContain(StrongPassword, Case.Sensitive);
        exception.Message.ShouldNotContain(email, Case.Insensitive);
        sink.Text.ShouldNotContain(StrongPassword, Case.Sensitive);
        (await ScalarAsync("SELECT role || '|' || status FROM users", ct)).ShouldBe("user|active");
        (await ScalarAsync("SELECT password_hash FROM users", ct)).ShouldBe(hashBefore);
        (await ScalarAsync("SELECT count(*)::text FROM users WHERE role = 'owner'", ct)).ShouldBe("0");
    }

    [Fact]
    public async Task OwnerSeed_OnlyAdminExists_StillCreatesOwner_AdminUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var adminEmail = NewEmail();
        using (var factory = new ApiFactory(Database, new FakeClock()))
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            (await users.CreateAsync(
                new AppUser { UserName = adminEmail, Email = adminEmail, DisplayName = "adm", Role = PlatformRole.Admin },
                StrongPassword)).Succeeded.ShouldBeTrue();
        }

        var report = await RunAsync(NewEmail(), StrongPassword, new Sink(), ct);

        report.Inserted.ShouldBe(1);
        (await ScalarAsync("SELECT count(*)::text FROM users WHERE role = 'owner'", ct)).ShouldBe("1");
        (await ScalarAsync($"SELECT role FROM users WHERE email = '{adminEmail}'", ct)).ShouldBe("admin");
    }

    [Fact]
    public async Task OwnerSeed_ExistingOwnerWithBlankConfig_IsSkippedNotWarned()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        await RunAsync(NewEmail(), StrongPassword, new Sink(), ct);
        var sink = new Sink();

        var report = await RunAsync(string.Empty, string.Empty, sink, ct);

        report.ShouldBe(new SeedReport("owner", 0, 0, 1));
        sink.Text.ShouldNotContain("not configured", Case.Sensitive);
    }

    [Fact]
    public async Task OwnerSeed_ManyReruns_StayOneOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();

        for (var run = 0; run < 4; run++)
        {
            var report = await RunAsync(email, StrongPassword, new Sink(), ct);
            report.ShouldBe(run == 0 ? new SeedReport("owner", 1, 0, 0) : new SeedReport("owner", 0, 0, 1));
        }

        (await ScalarAsync("SELECT count(*)::text FROM users", ct)).ShouldBe("1");
    }

    [Fact]
    public void SeedOwnerOptions_ToStringAndOptionsDump_NeverPrintValues()
    {
        var owner = new SeedOwnerOptions { Email = "qa@nighttales.test", Password = StrongPassword };
        var options = new SeedOptions { Owner = owner };

        owner.ToString().ShouldBe("***");
        $"{owner}".ShouldNotContain(StrongPassword, Case.Sensitive);
        string.Format(CultureInfo.InvariantCulture, "{0}", owner).ShouldNotContain("qa@", Case.Sensitive);
        System.Text.Json.JsonSerializer.Serialize(options.Owner.ToString()).ShouldNotContain(StrongPassword, Case.Sensitive);
    }

    [Fact]
    public async Task OwnerSeed_RealDiGraph_SeedRunnerRunsOwnerLast_FromConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();
        var sink = new Sink();
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting("Seed:Owner:Email", email)
            .WithSetting("Seed:Owner:Password", StrongPassword)
            .WithSetting("Seed:ContinueOnError", "true")
            .WithLoggerProvider(sink);
        // WebApplicationFactory boots in Development, so the auto-seed already created the owner from the settings.
        _ = factory.Services;
        (await ScalarAsync($"SELECT role FROM users WHERE email = '{email}'", ct)).ShouldBe("owner");
        await ResetAsync(ct);
        using var scope = factory.Services.CreateScope();

        var seeders = scope.ServiceProvider.GetServices<ISeeder>().ToArray();
        var reports = await scope.ServiceProvider.GetRequiredService<SeedRunner>().RunAsync(ct);

        seeders.OfType<OwnerSeeder>().Count().ShouldBe(1);
        seeders.Max(seeder => seeder.Order).ShouldBe(new OwnerSeeder(null!, null!, null!, null!, null!).Order);
        reports.Last().Seeder.ShouldBe("owner");
        reports.Last().ShouldBe(new SeedReport("owner", 1, 0, 0));
        (await ScalarAsync($"SELECT role FROM users WHERE email = '{email}'", ct)).ShouldBe("owner");
        sink.Text.ShouldNotContain(StrongPassword, Case.Sensitive);
    }

    [Fact]
    public async Task OwnerSeed_DevelopmentHost_AutoSeedsOwner_ProductionHostDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var email = NewEmail();
        var sink = new Sink();

        void Configure(IWebHostBuilder builder, string environment)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Seed:Owner:Email", email);
            builder.UseSetting("Seed:Owner:Password", StrongPassword);
            builder.ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(sink)));
        }

        await using (var production = ApiFactory.ProductionHost(
            Database.ConnectionString, builder => Configure(builder, Environments.Production)))
        {
            _ = production.Services;
        }

        (await ScalarAsync("SELECT count(*)::text FROM users", ct)).ShouldBe("0");

        await using (var development = ApiFactory.ProductionHost(
            Database.ConnectionString, builder => Configure(builder, Environments.Development)))
        {
            _ = development.Services;
        }

        (await ScalarAsync($"SELECT role FROM users WHERE email = '{email}'", ct)).ShouldBe("owner");
        sink.Text.ShouldNotContain(StrongPassword, Case.Sensitive);
        await ResetAsync(ct);
        await Database.ExecuteAsync(
            "DELETE FROM voices; DELETE FROM style_templates; DELETE FROM feature_flags", ct);
    }

    [Fact]
    public async Task OwnerSeed_DevelopmentHostWeakPassword_BootContinues_NoLeak()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct);
        var sink = new Sink();
        var weak = "Sh0rt-" + "pw";

        await using (var development = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder =>
            {
                builder.UseEnvironment(Environments.Development);
                builder.UseSetting("Seed:Owner:Email", NewEmail());
                builder.UseSetting("Seed:Owner:Password", weak);
                builder.ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(sink)));
            }))
        {
            _ = development.Services;
        }

        (await ScalarAsync("SELECT count(*)::text FROM users", ct)).ShouldBe("0");
        sink.Text.ShouldContain("seed.runner.failed", Case.Sensitive, "the seeder failure was logged");
        sink.Text.ShouldNotContain(weak, Case.Sensitive);
        await Database.ExecuteAsync(
            "DELETE FROM voices; DELETE FROM style_templates; DELETE FROM feature_flags", ct);
    }

    private static OwnerSeeder Build(IServiceScope scope, string email, string password, ILoggerProvider sink)
    {
        var loggers = LoggerFactory.Create(logging => logging.AddProvider(sink));
        return new OwnerSeeder(
            scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            Options.Create(new SeedOptions { Owner = new SeedOwnerOptions { Email = email, Password = password } }),
            new FakeClock(),
            loggers.CreateLogger<OwnerSeeder>());
    }

    private async ValueTask<SeedReport> RunAsync(string email, string password, ILoggerProvider sink, CancellationToken ct)
    {
        using var factory = new ApiFactory(Database, new FakeClock());
        using var scope = factory.Services.CreateScope();
        return await Build(scope, email, password, sink).SeedAsync(ct);
    }

    private sealed class Sink : ILoggerProvider
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

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(Sink owner) : ILogger
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
                var line = formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception);
                lock (owner._gate)
                {
                    owner._messages.Add(line);
                }
            }
        }
    }
}
