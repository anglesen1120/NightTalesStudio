namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// The one-way function behind <c>email_messages.to_hash</c> (09 README BD9/BD16, 09.2.6-T3): the hex
/// SHA-256 of the lower-cased, trimmed recipient address with the platform's address pepper.
/// </summary>
/// <remarks>
/// <para>
/// The pepper is configuration (a secret of the deployment, <c>Email:AddressHashPepper</c>), so it lives
/// behind this port: the Application layer composes the message and never sees a secret. Two
/// environments with different peppers produce different hashes for the same address, which is what makes
/// the value useless to anyone who reads only the database.
/// </para>
/// <para>
/// The hash is what appears in logs and in the delivery record — the address itself never does (09 README
/// BD16). It is deliberately not reversible: correlating rows needs the address again, not the hash.
/// </para>
/// </remarks>
public interface IEmailAddressHasher
{
    /// <summary>Hashes one recipient address.</summary>
    /// <param name="address">The address as <c>users.email</c> stores it; case and surrounding blanks are normalized here.</param>
    /// <returns>The lower-case hex SHA-256 of the normalized address with the pepper.</returns>
    string Hash(string address);
}
