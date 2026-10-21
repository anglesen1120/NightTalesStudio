using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>data_protection_keys</c> table (09 README WF15, 09.9.4-T1 plan §3, 00 §6:271): the framework's
/// key ring, one row per key.
/// </summary>
/// <remarks>
/// <para>
/// The shape is fixed by the framework's own repository: an identity <c>id</c>, the <c>friendly_name</c>
/// the key manager looks a key up by (unique), and the <c>xml</c> payload. The XML is
/// <em>already encrypted</em> with the <c>DP_KEK</c> certificate before it reaches this column
/// (<c>ProtectKeysWithCertificate</c>), so a database dump alone cannot unprotect anything.
/// </para>
/// <para>
/// Both members are nullable in the framework's entity (it is a plain POCO), and both are required here:
/// a key without a name is unreachable and a key without a payload is nothing, so the column says so and
/// the database refuses the row instead of the key manager failing at read time.
/// </para>
/// <para>
/// The table is platform data, not user data: it is not <c>IUserOwned</c>, so the tenant filter of
/// 09.2.3-T1 (D10) does not touch it — the key manager runs outside any request scope.
/// </para>
/// </remarks>
internal sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        builder.ToTable("data_protection_keys");
        builder.HasKey(key => key.Id);

        // `friendly_name` is what `EFCoreXmlRepository` filters on (`FriendlyName == name`), so its index
        // is unique *and* is the lookup path; the column bound matches the framework's own 256-character
        // friendly names.
        builder.Property(key => key.FriendlyName).IsRequired().HasMaxLength(256);
        builder.HasIndex(key => key.FriendlyName).IsUnique();

        // `text`: the encrypted XML is not bounded and is never queried by content (D8 keeps string
        // columns as `text` unless a bound is part of the contract).
        builder.Property(key => key.Xml).IsRequired();
    }
}
