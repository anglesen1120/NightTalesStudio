using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Security;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// The <c>ITokenProtector</c> over the real Data Protection key ring (09.9.4-T1 Tests): a purpose is
/// the isolation boundary, the ring lives in <c>data_protection_keys</c> encrypted with the KEK, and
/// two hosts that share the KEK share the ring.
/// </summary>
/// <remarks>
/// <para>
/// Every host here is built the way a composition root builds one — a service collection that gets
/// <see cref="DataProtectionServiceCollectionExtensions.AddNightTalesDataProtection"/> over a real
/// <see cref="AppDbContext"/> pointed at this class's migrated database — so what is asserted is the
/// production wiring and not a hand-made <c>IDataProtectionProvider</c>.
/// </para>
/// <para>
/// The KEK is <see cref="TestKek"/>: a throwaway self-signed RSA PFX generated for this run, never a
/// production certificate and never a committed file.
/// </para>
/// <para>
/// The TOTP secret store of 09.10.4-T1 does not appear here as a caller: it uses
/// <c>IDataProtectionProvider.CreateProtector("identity.totp.v1")</c> directly, so the property this
/// class proves is the one that matters for it — that purpose is unreachable from any
/// <c>tokens.v1/*</c> purpose and vice versa.
/// </para>
/// </remarks>
public sealed class TokenProtectorTests(TestDatabase database) : IntegrationTest(database)
{
    private const string ConnectionSection = $"ConnectionStrings:{PersistenceOptions.ConnectionStringName}";
    private const string Purpose = "c1";
    private const string OtherPurpose = "c2";
    private const string ApplicationName = "nts-token-protector-tests";
    private const string TotpPurposeRoot = "identity.totp.v1";

    [Fact]
    public void Round_trip_with_same_purpose()
    {
        using var host = BuildHost();
        var protector = Protector(host);

        var cipher = protector.Protect(Purpose, "value");

        cipher.ShouldNotContain("value");
        protector.TryUnprotect(Purpose, cipher, out var plaintext).ShouldBeTrue();
        plaintext.ShouldBe("value");
    }

    [Fact]
    public void Different_purpose_fails()
    {
        using var host = BuildHost();
        var protector = Protector(host);

        var cipher = protector.Protect(Purpose, "value");

        protector.TryUnprotect(OtherPurpose, cipher, out var plaintext).ShouldBeFalse();
        plaintext.ShouldBeNull();
    }

    [Fact]
    public void Empty_plaintext_rejected()
    {
        using var host = BuildHost();

        Should.Throw<ArgumentException>(() => Protector(host).Protect(Purpose, string.Empty));
    }

    [Fact]
    public void Null_purpose_rejected()
    {
        using var host = BuildHost();
        var protector = Protector(host);

        Should.Throw<ArgumentException>(() => protector.Protect(null!, "value"));
        Should.Throw<ArgumentException>(() => protector.TryUnprotect(null!, "cipher", out _));
    }

    [Fact]
    public void Tampered_cipher_fails()
    {
        using var host = BuildHost();
        var protector = Protector(host);
        var cipher = protector.Protect(Purpose, "value");

        // Eight base64 characters are six bytes: the truncation cannot leave the same payload behind.
        var tampered = cipher[..^8];

        protector.TryUnprotect(Purpose, tampered, out var plaintext).ShouldBeFalse();
        plaintext.ShouldBeNull();
        protector.TryUnprotect(Purpose, "not-base64", out _).ShouldBeFalse();
    }

    [Fact]
    public void Tampered_cipher_of_the_same_length_fails()
    {
        using var host = BuildHost();
        var protector = Protector(host);
        var cipher = protector.Protect(Purpose, "value");

        // Flip one base64 character in the middle: the payload keeps its length and stays valid base64,
        // so only the framework's authenticated encryption can refuse it — this is not a decode failure.
        var index = cipher.Length / 2;
        var flipped = cipher[index] == 'A' ? 'B' : 'A';
        var tampered = string.Concat(cipher[..index], flipped.ToString(), cipher[(index + 1)..]);

        tampered.Length.ShouldBe(cipher.Length);
        protector.TryUnprotect(Purpose, tampered, out var plaintext).ShouldBeFalse();
        plaintext.ShouldBeNull();
    }

    [Fact]
    public void Empty_purpose_rejected()
    {
        using var host = BuildHost();
        var protector = Protector(host);

        // A whitespace suffix would produce the purpose `tokens.v1/ `, a distinct isolation root a
        // caller can reach by accident: both the empty and the whitespace-only suffix are refused.
        Should.Throw<ArgumentException>(() => protector.Protect(string.Empty, "value"));
        Should.Throw<ArgumentException>(() => protector.TryUnprotect(string.Empty, "cipher", out _));
        Should.Throw<ArgumentException>(() => protector.Protect("   ", "value"));
        Should.Throw<ArgumentException>(() => protector.TryUnprotect("   ", "cipher", out _));
    }

    [Fact]
    public void No_value_in_logs()
    {
        const string Secret = "value-that-must-never-be-logged";
        var sink = new CaptureLoggerProvider();
        using var host = BuildHost(loggerProvider: sink);
        var protector = Protector(host);

        var cipher = protector.Protect(Purpose, Secret);

        // A wrong purpose is the failing path that writes the one log line this type has.
        protector.TryUnprotect(OtherPurpose, cipher, out _).ShouldBeFalse();

        sink.Messages.ShouldNotContain(message => message.Contains(Secret, StringComparison.Ordinal));
        sink.Messages.ShouldNotContain(message => message.Contains(cipher, StringComparison.Ordinal));
        // The purpose is an identifier the caller chose and not a value: it is what the line names.
        sink.Messages.ShouldContain(message => message.Contains(OtherPurpose, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Key_ring_row_created_on_first_protect()
    {
        // The class shares one database and the hosts of the class's earlier tests have already written
        // their own ring rows (a host with another KEK cannot read this ring and writes one of its own),
        // so the table is emptied first: the assertion is about the row this Protect creates.
        await ClearKeyRingAsync();
        using var host = BuildHost();

        Protector(host).Protect(Purpose, "value");

        (await RowCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Row_count_grows_only_by_key_rotation()
    {
        await ClearKeyRingAsync();
        using var host = BuildHost();
        var protector = Protector(host);

        protector.Protect(Purpose, "one");
        protector.Protect(OtherPurpose, "two");

        // One key serves every purpose, so the ring grows when the framework rotates (the 90-day
        // default), not per purpose and not per Protect call — and nothing in the platform prunes the
        // table: a removed key makes the values protected with it unrecoverable (plan §11). The second
        // Protect of one host reuses the ring the first one created, so the emptied table holds exactly
        // one row afterwards.
        (await RowCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Xml_column_has_no_plaintext()
    {
        const string Plaintext = "nts-canary-plaintext-7f31";
        await ClearKeyRingAsync();
        using var host = BuildHost();

        Protector(host).Protect(Purpose, Plaintext);

        var stored = (await Database.QueryStringsAsync(
            "SELECT xml FROM data_protection_keys",
            TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        // WF15: the marked secret of the ring is encrypted with the KEK certificate before it reaches the
        // column (`ProtectKeysWithCertificate` replaces the marked element with an <encryptedSecret>
        // holding the EncryptedXml payload), so neither the protected value nor readable key material is
        // in the row. The envelope itself — the key id, the descriptor element and its deserializer type —
        // stays plaintext XML, so the assertion is on the value and on the encryption marker, not on an
        // element name the format always carries (`CipherValue` contains "Value", for one).
        stored.ShouldContain("encryptedSecret");
        stored.ShouldContain("EncryptedData");
        stored.ShouldNotContain(Plaintext);
    }

    [Fact]
    public void Second_instance_with_same_kek_unprotects()
    {
        using var first = BuildHost();
        var cipher = Protector(first).Protect(Purpose, "value");

        // A second host: its own service provider, its own key manager, the same KEK (TestKek.SharedPath)
        // and the same ring rows — exactly the Api/Worker pair of 11 §10:284.
        using var second = BuildHost();

        Protector(second).TryUnprotect(Purpose, cipher, out var plaintext).ShouldBeTrue();
        plaintext.ShouldBe("value");
    }

    [Fact]
    public void Wrong_certificate_cannot_unprotect()
    {
        using var first = BuildHost();
        var cipher = Protector(first).Protect(Purpose, "value");

        // A different KEK over the same ring: the ring XML is unreadable, so no key of this ring is
        // usable and the cipher is refused — the failure mode a mismatched DP_KEK shows as.
        using var other = BuildHost(TestKek.Create("nts-test-other-dp-kek"));

        Protector(other).TryUnprotect(Purpose, cipher, out var plaintext).ShouldBeFalse();
        plaintext.ShouldBeNull();
    }

    [Fact]
    public void Identity_totp_v1_purpose_is_isolated_from_tokens_v1()
    {
        using var host = BuildHost();
        var provider = host.GetRequiredService<IDataProtectionProvider>();
        var tokens = Protector(host);

        // The TOTP store's own purpose root, exactly as 09.10.4-T1 calls it (README §B10 WT43).
        var totp = provider.CreateProtector(TotpPurposeRoot);
        var totpCipher = Convert.ToBase64String(totp.Protect(Encoding.UTF8.GetBytes("totp-secret")));

        tokens.TryUnprotect(TotpPurposeRoot, totpCipher, out _).ShouldBeFalse();
        tokens.TryUnprotect(Purpose, totpCipher, out _).ShouldBeFalse();

        var tokensCipher = tokens.Protect(Purpose, "value");
        Should.Throw<CryptographicException>(() => totp.Unprotect(Convert.FromBase64String(tokensCipher)));
    }

    private static ITokenProtector Protector(ServiceProvider host) =>
        host.GetRequiredService<ITokenProtector>();

    private async Task<long> RowCountAsync()
    {
        var rows = await Database.QueryStringsAsync(
            "SELECT count(*)::text FROM data_protection_keys",
            TestContext.Current.CancellationToken);

        return long.Parse(rows.ShouldHaveSingleItem(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Empties the ring of this class's database, so an assertion about the rows one test creates does
    /// not depend on the hosts the class's earlier tests already built (xUnit runs the tests of one
    /// class one after another, and no other class shares this database).
    /// </summary>
    private async Task ClearKeyRingAsync() =>
        await Database.ExecuteAsync("DELETE FROM data_protection_keys", TestContext.Current.CancellationToken);

    /// <summary>
    /// A host whose only persistence is the key ring's own context (the ring's repository resolves
    /// <see cref="AppDbContext"/> from a scope of its own), with the production Data Protection
    /// registration over the class database.
    /// </summary>
    private ServiceProvider BuildHost(
        TestKekFile? kek = null,
        ILoggerProvider? loggerProvider = null)
    {
        var key = kek ?? new TestKekFile(TestKek.SharedPath, TestKek.SharedPassword);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ConnectionSection] = Database.ConnectionString,
                [$"{DataProtectionKeyRingOptions.SectionName}:{nameof(DataProtectionKeyRingOptions.KekPfxPath)}"] = key.Path,
                [$"{DataProtectionKeyRingOptions.SectionName}:{nameof(DataProtectionKeyRingOptions.KekPfxPassword)}"] = key.Password,
                [$"{DataProtectionKeyRingOptions.SectionName}:{nameof(DataProtectionKeyRingOptions.ApplicationName)}"] = ApplicationName,
            })
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<ICurrentUser>(new AnonymousCurrentUser());
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(Database.ConnectionString)
            .UseSnakeCaseNamingConvention());
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);

            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });

        services.AddNightTalesDataProtection(configuration);

        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Integration";

        public string ApplicationName { get; set; } = "NightTales.Infrastructure.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>
    /// The capture sink of this test class (09.9.3-T2's pipeline sink does not exist yet): it keeps
    /// every formatted log line so a test can assert that no value reached one.
    /// </summary>
    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

        public void Dispose()
        {
            // Nothing to release: the sink is a list.
        }

        private sealed class CaptureLogger(CaptureLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                lock (provider._messages)
                {
                    provider._messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
