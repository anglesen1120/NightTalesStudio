using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Catalog.Queries.ListStyleTemplates;
using NightTales.Application.Features.Catalog.Queries.ListVoices;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Catalog;

/// <summary>
/// The 09.4.2-T1 acceptance checks that are about the handlers rather than about a response (plan §9
/// "Catalog_Queries_Have_No_Write_Dependency", plan §3 Q13/BD16): one read per request, no tracking,
/// and no write port anywhere in their dependency set.
/// </summary>
/// <remarks>
/// <para>
/// The handlers are driven directly, with a hand-written decorating <see cref="IReadDb"/> (D13: no
/// mocking library) over the real no-tracking <c>ReadDbContext</c>. One <see cref="IReadDb.Query{TEntity}"/>
/// enumeration is one SQL command: EF issues no split query here, and neither <c>Voice</c> nor
/// <c>StyleTemplate</c> has a navigation property an <c>Include</c> could fan out.
/// </para>
/// <para>
/// The route suites cover the filters and the answers over HTTP; this file pins the parts that are
/// about the handlers and the pipeline contract itself: one read per request, no tracking, no write
/// port in a dependency set, and the wire field of a rejected filter.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CatalogQueryContractTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Handlers_Read_One_Query_And_Track_Nothing()
    {
        var voiceId = $"nts-v-{Guid.CreateVersion7():N}";
        await SeedVoiceAsync(voiceId);
        var templateName = $"nts-t1-{Guid.CreateVersion7():N}";
        await SeedTemplateAsync(templateName);

        await using var readContext = Database.CreateReadDbContext(new TestCurrentUser());
        var counting = new CountingReadDb(readContext);

        var voices = await new ListVoicesHandler(counting, new StubPublicAssetUrl())
            .Handle(new ListVoicesQuery(null), Ct);

        voices.IsError.ShouldBeFalse("the catalog query must succeed");
        counting.Queries.ShouldBe(1, "the whole catalog is one round trip (plan §3 Q13)");
        voices.Value.ShouldContain(item => item.Id == voiceId);
        readContext.ChangeTracker.Entries().ShouldBeEmpty("the read model never tracks (09 README D8)");

        var templates = await new ListStyleTemplatesHandler(counting, new StubPublicAssetUrl())
            .Handle(new ListStyleTemplatesQuery("ghost"), Ct);

        templates.IsError.ShouldBeFalse("the template catalog query must succeed");
        counting.Queries.ShouldBe(2, "one round trip for the template catalog too");
        templates.Value.ShouldContain(item => item.Name == templateName);
        readContext.ChangeTracker.Entries().ShouldBeEmpty("the read model never tracks (09 README D8)");
    }

    [Fact]
    public async Task Validators_Reject_Empty_Filters_Under_The_Wire_Field()
    {
        using var factory = new ApiFactory(Database, new FakeClock(Start));
        await using var scope = factory.Services.CreateAsyncScope();

        // An in-process sender has no HttpContext, so the D4 authorization step would answer 401 before
        // the validator; pinning the scope with the same ISystemContext.For the outbox path uses names
        // the caller, and the empty filter then reaches the validator.
        var system = scope.ServiceProvider.GetRequiredService<ISystemContext>();
        using var pinned = system.For(Guid.CreateVersion7());
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // An explicitly empty `?language=` / `?topic=` is sent in-process: over HTTP the value provider may
        // deliver an empty value or nothing at all (the D4 pipeline cannot tell those apart), so the
        // validator's contract of 09.4.2 plan §Failure handling is pinned here, while the route suites
        // cover the values that always survive binding.
        AssertRejected(await sender.Send(new ListVoicesQuery(string.Empty), Ct), "language");
        AssertRejected(await sender.Send(new ListStyleTemplatesQuery(string.Empty), Ct), "topic");
    }

    [Fact]
    public void Handlers_Depend_Only_On_The_Read_Ports()
    {
        // A query handler that could write would have to name IAppDbContext or IUnitOfWork; neither
        // handler does, and both read through IReadDb, which exposes no SaveChanges at all (D8).
        DependenciesOf(typeof(ListVoicesHandler))
            .ShouldBe([typeof(IReadDb), typeof(IPublicAssetUrl)], ignoreOrder: true);
        DependenciesOf(typeof(ListStyleTemplatesHandler))
            .ShouldBe([typeof(IReadDb), typeof(IPublicAssetUrl)], ignoreOrder: true);
    }

    private static Type[] DependenciesOf(Type handler) =>
        [.. handler.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)];

    /// <summary>Asserts the D7 shape of a rejected filter: <c>validation_failed</c> under one field.</summary>
    private static void AssertRejected<T>(ErrorOr<T> result, string field)
    {
        result.IsError.ShouldBeTrue("an unknown or empty filter must be refused before the handler runs");
        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.FirstError.Metadata![ErrorMetadata.Field].ShouldBe(field);
    }

    private async Task SeedVoiceAsync(string id)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser());
        context.Voices.Add(Voice.Create(id, id, Language.Vi, VoiceGender.Female));
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedTemplateAsync(string name)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser());
        context.StyleTemplates.Add(StyleTemplate.Create(
            name,
            "Một khung cảnh {term} được kể bằng nét vẽ đơn sắc trên nền giấy cũ, ánh sáng dịu và bố cục rộng.",
            topicHint: null));
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Counts the read-model queries a handler issues, and delegates the work unchanged.</summary>
    private sealed class CountingReadDb(IReadDb inner) : IReadDb
    {
        public int Queries { get; private set; }

        public IQueryable<TEntity> Query<TEntity>()
            where TEntity : class
        {
            Queries++;

            return inner.Query<TEntity>();
        }
    }

    /// <summary>
    /// A public asset URL builder that always composes a URL: this file is about the query, not about
    /// the composition, which 09.4.2-T1's unit tests pin on their own.
    /// </summary>
    private sealed class StubPublicAssetUrl : IPublicAssetUrl
    {
        public Uri? Build(string? storageKey) =>
            storageKey is null ? null : new Uri($"https://nt-public-dev.example.test/{storageKey}");
    }
}
