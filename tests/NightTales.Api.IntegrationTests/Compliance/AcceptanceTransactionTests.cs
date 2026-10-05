using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Identity;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

public sealed class AcceptanceTransactionTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accept_FailingAuditWriter_RollsBackConsentRows()
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        using var factory = new ApiFactory(Database, new FakeClock()).WithServices(services =>
        {
            services.RemoveAll<IAuditWriter>();
            services.AddScoped<IAuditWriter, ThrowingAuditWriter>();
        });
        using var client = factory.ClientFor(user.Id);
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.PublicOrigin);
        using var response = await client.PostAsJsonAsync("/api/v1/me/legal/accept", new { tos = "1.0.0", privacy = "1.0.0" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.legal_accepted'").ShouldBe(0);
    }

    [Fact]
    public async Task Accept_UserDeletedBeforeCommit_ReturnsValidationFailedAndWritesNothing()
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);
        using var factory = new ApiFactory(Database, new FakeClock()).WithServices(services =>
        {
            services.RemoveAll<IConsentWriter>();
            services.AddScoped<IConsentWriter>(provider => new DeletingConsentWriter(
                new ConsentWriter(provider.GetRequiredService<AppDbContext>(), provider.GetRequiredService<IClock>()), Database));
        });
        using var client = factory.ClientFor(user.Id);
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.PublicOrigin);
        using var response = await client.PostAsJsonAsync("/api/v1/me/legal/accept", new { tos = "1.0.0", privacy = "1.0.0" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.ShouldContain("validation_failed");
        text.ShouldNotContain("23503");
        text.ShouldNotContain("foreign key", Case.Insensitive);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_consents WHERE user_id = '{user.Id}'").ShouldBe(0);
    }

    private sealed class ThrowingAuditWriter : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("forced audit failure");
    }

    private sealed class DeletingConsentWriter(IConsentWriter inner, TestDatabase database) : IConsentWriter
    {
        public async Task<ErrorOr<Success>> RecordAsync(Guid userId, LegalDocument document, string version, IPAddress? ip, CancellationToken ct)
        {
            var result = await inner.RecordAsync(userId, document, version, ip, ct);
            if (document == LegalDocument.Privacy)
            {
                SqlProbe.Execute(database, $"DELETE FROM users WHERE id = '{userId}'");
            }
            return result;
        }
    }
}
