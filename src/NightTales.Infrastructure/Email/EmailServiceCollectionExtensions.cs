using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Features.Email;
using NightTales.Application.Features.Email.Outbox;
using NightTales.Application.Features.Email.TokenMinters;
using NightTales.Domain.Communication;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// Registers the e-mail pipeline (09 README BD9, 09.2.6-T3): the options and their start-up validation, the
/// validated template set, the renderer, the address hasher, the SMTP adapter, the token minter of the
/// reset template and the <c>email_requested.v1</c> consumer.
/// </summary>
/// <remarks>
/// <para>
/// Both composition roots call this. The Api needs the queueing half (<c>IEmailSender</c>,
/// <c>IEmailDeliveryStatus</c>) because its handlers are the producers; the Worker needs the sending half
/// (<c>SendEmailJob</c>, the renderer, the SMTP adapter). Registering one graph keeps the two hosts from
/// disagreeing about a template or an option, and it costs the Api nothing: nothing connects to SMTP until
/// a job runs.
/// </para>
/// <para>
/// It is called from the composition roots and not from <c>AddPersistence</c>, so the design-time model, the
/// migration tests and the model unit tests — none of which send anything — need no <c>Email</c>
/// configuration.
/// </para>
/// </remarks>
public static class EmailServiceCollectionExtensions
{
    /// <summary>Registers the e-mail pipeline of the host.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration (<c>Email:*</c>, <c>App:PublicOrigin</c>).</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException"><c>App:PublicOrigin</c> is absent or unusable.</exception>
    /// <exception cref="EmailTemplateException">The shipped template set does not match the catalog.</exception>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        EmailOptionsValidator.Apply(
            services.AddOptions<EmailOptions>().Configure(options => EmailOptions.Bind(configuration, options)));

        services.AddSingleton<EmailMetrics>();

        // The template set is loaded and validated while the composition root runs, so a missing file, an
        // orphan file, an undeclared placeholder or a text file without its subject line stops the boot
        // instead of producing a message with a literal placeholder in it (09 README BD9).
        services.AddSingleton(EmailTemplateProvider.FromDeployedFiles());

        // Read when a link is first composed — the send-time mint — not while the composition runs: the
        // Api already refuses to start on this same key through 09.9.4-T1's `App` options (its CORS
        // allow-list), and a host that never mints a link needs no origin at all.
        services.AddSingleton<IEmailLinkOrigin>(_ => ConfigurationEmailLinkOrigin.From(configuration));
        services.AddSingleton<IEmailFromAddress>(provider => CreateFromAddress(provider));

        services.AddScoped<IEmailSender, EmailSender>();
        services.AddScoped<IEmailDeliveryStatus, EmailDeliveryStatusQuery>();
        services.AddScoped<IEmailRenderer, EmailRenderer>();
        services.AddScoped<IEmailAddressHasher, Sha256EmailAddressHasher>();
        services.AddScoped<IEmailRecipientQuery, EmailRecipientQuery>();
        services.AddScoped<IEmailTokenMinters, EmailTokenMinters>();
        services.AddScoped<IEmailSendLock, PostgresEmailSendLock>();
        services.AddScoped<ISmtpEmailSender, MailKitSmtpEmailSender>();

        // One keyed minter per token-bearing template (09 README §B10 WT17). Keyed and not enumerable: the
        // minter of a token-bearing template is resolved only when that template is actually sent, so a
        // template whose minter lands later (09.3.1-T3's email_verify) cannot break the rest of the pipeline.
        // Registered through a factory, not by implementation type, for the same reason: the reset minter's
        // dependency is 09.3.3-T1's `IUserActionTokenService`, which does not exist in this build, and a
        // host that never sends a `password_reset` message — the Api — must not fail to start over it.
        // `ValidateOnBuild` walks implementation types only; a factory's dependencies are resolved on first use.
        services.AddKeyedScoped<IEmailTokenMinter>(
            PasswordResetTokenMinter.TemplateName,
            static (provider, _) => new PasswordResetTokenMinter(
                provider.GetRequiredService<IUserActionTokenService>(),
                provider.GetRequiredService<IEmailLinkOrigin>()));

        // The Worker's own outbox loop is what invokes it; a host that only writes outbox rows — the Api —
        // never builds the handler, because it is only constructed for a message of its event type.
        services.AddOutboxHandler<EmailRequested, EmailRequestedHandler>();

        return services;
    }

    /// <summary>
    /// Builds the sender mailbox from the validated options; the parse cannot fail after the start-up
    /// validation, and a failure here would still be a boot failure rather than a failed job.
    /// </summary>
    /// <param name="provider">The container, for the bound options.</param>
    private static ConfigurationEmailFromAddress CreateFromAddress(IServiceProvider provider)
    {
        var from = provider.GetRequiredService<IOptions<EmailOptions>>().Value.From;

        if (!MailboxAddress.TryParse(from.Address, out var mailbox))
        {
            throw new InvalidOperationException(EmailOptionsValidator.UnusableFromAddressFailure);
        }

        return new ConfigurationEmailFromAddress(mailbox.Address, from.Name);
    }
}
