namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// The origin every link inside an e-mail is composed from (09 README BD9, 09.2.6-T3): the same
/// <c>App:PublicOrigin</c> the Api serves the SPA from and the one 09.9.4-T1 validates.
/// </summary>
/// <remarks>
/// <para>
/// Links are built at send time and never travel through <c>email_messages.params</c> or the outbox
/// payload (09 README §B10 WT17): a token-bearing link would otherwise sit in the database and in the
/// queue table until the message was sent.
/// </para>
/// <para>
/// It is a port because the Worker composes links too and must not reach into the Api host's options: the
/// infrastructure implementation reads the configuration key directly, so an address in a mail and a CORS
/// decision in the Api can never disagree about which origin the platform is served from.
/// </para>
/// </remarks>
public interface IEmailLinkOrigin
{
    /// <summary>The absolute origin, for example <c>https://app.nighttales.app</c>.</summary>
    Uri Origin { get; }
}
