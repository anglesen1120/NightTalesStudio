using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// The tenant filter of 09 README D10: every entity type implementing <see cref="IUserOwned"/> gets
/// the named query filter <c>"tenant"</c> — <c>UserId == the scope's user</c> — on the write model and
/// on the read model alike.
/// </summary>
/// <remarks>
/// <para>
/// Applied by <see cref="ModelBuilderExtensions.ApplyNightTalesConventions"/> from both contexts, so
/// the filter cannot be missing on one of them. It is a convention, not per-query code: a handler
/// that forgets a <c>Where</c> still cannot read another user's rows.
/// </para>
/// <para>
/// The body reads the <em>context instance</em> member (<c>AppDbContext.CurrentUserId</c> /
/// <c>ReadDbContext.CurrentUserId</c>) rather than a captured value. EF replaces that instance
/// reference with the current context at query time, so the filter is a parameter of every statement
/// and the model EF caches per context type never bakes in the first scope's user (plan §2 T2f).
/// </para>
/// <para>
/// Allow-listed bypasses (09.1.2-T1) disable exactly this filter by name, so a future named filter
/// stays active beside it.
/// </para>
/// </remarks>
internal static class TenantFilterConvention
{
    /// <summary>The name of the filter; every bypass disables this one by name (09 README D10).</summary>
    internal const string FilterName = "tenant";

    private static readonly PropertyInfo CurrentUserIdProperty =
        typeof(ITenantFilterContext).GetProperty(nameof(ITenantFilterContext.CurrentUserId))!;

    /// <summary>Adds the <c>"tenant"</c> filter to every <see cref="IUserOwned"/> entity type.</summary>
    /// <param name="modelBuilder">The model being built.</param>
    /// <param name="context">The context being built — the filter's user comes from it.</param>
    internal static void Apply(ModelBuilder modelBuilder, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        // The constant is deliberately typed as the interface and holds the context instance: EF
        // recognizes it as "an access to the current context" and parameterizes the filter.
        var currentUserId = Expression.Constant(context, typeof(ITenantFilterContext));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (!typeof(IUserOwned).IsAssignableFrom(clrType))
            {
                continue;
            }

            // The concrete property, not the interface member: EF binds a member access on the entity
            // type's own CLR type, which is what maps to the `user_id` column.
            var ownerId = clrType.GetProperty(nameof(IUserOwned.UserId))
                ?? throw new InvalidOperationException(
                    $"'{clrType.FullName}' implements IUserOwned but exposes no public UserId property, so the tenant filter of 09 README D10 cannot be built.");

            var entity = Expression.Parameter(clrType, "entity");
            var body = Expression.Equal(
                // Lifted on purpose: the entity's user_id is never null, so an anonymous scope (a null
                // current user) matches no row instead of throwing.
                Expression.Convert(Expression.Property(entity, ownerId), typeof(Guid?)),
                Expression.Property(currentUserId, CurrentUserIdProperty));

            modelBuilder.Entity(clrType).HasQueryFilter(FilterName, Expression.Lambda(body, entity));
        }
    }
}

/// <summary>
/// The context member the tenant filter reads: <c>AppDbContext</c> and <c>ReadDbContext</c> capture
/// the scope's user (<c>ICurrentUser.Id</c>) here when they are constructed (09 README D10). It is an
/// interface only so one convention can serve both contexts without reflection.
/// </summary>
internal interface ITenantFilterContext
{
    /// <summary>The owner every filtered query of this context is restricted to; <c>null</c> means anonymous, which sees no user-owned row.</summary>
    Guid? CurrentUserId { get; }
}
