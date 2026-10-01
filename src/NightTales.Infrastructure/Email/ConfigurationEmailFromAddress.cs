using MimeKit;
using NightTales.Application.Abstractions.Email;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// The composed <c>From</c> mailbox (09 README BD9, 09.2.6-T3): the validated
/// <c>Email:From:Address</c> with the configured display name.
/// </summary>
/// <remarks>
/// The address is parsed once, while the composition root runs, so a malformed value is a boot failure
/// (<c>EmailOptionsValidator</c>) and a job never composes a message the server would refuse. The display
/// name is applied here, which is why the configured address is taken as an addr-spec and not as a whole
/// mailbox.
/// </remarks>
internal sealed class ConfigurationEmailFromAddress : IEmailFromAddress
{
    /// <summary>Builds the sender mailbox.</summary>
    /// <param name="address">The validated <c>Email:From:Address</c> in its addr-spec form.</param>
    /// <param name="displayName">The <c>Email:From:Name</c> next to it.</param>
    public ConfigurationEmailFromAddress(string address, string displayName) =>
        From = new MailboxAddress(displayName, address);

    /// <inheritdoc />
    public MailboxAddress From { get; }
}
