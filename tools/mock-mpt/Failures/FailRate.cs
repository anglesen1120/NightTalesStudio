using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NightTales.MockMpt.Failures;

/// <summary>
/// <c>MOCK_FAIL_RATE</c> (11.1.2-T2 Design): a float in <c>[0,1]</c> and the deterministic per-submit draw. The
/// draw hashes the submit's <c>render_job_id</c> — never a shared process RNG — so a test asserting "N of M
/// submissions fail" is exact, a rerun is identical and concurrent submits never race.
/// </summary>
public static class FailRate
{
    /// <summary>
    /// The modes a rate-selected failure is chosen from, in the fixed order of the Design: the draw, rescaled
    /// over <c>[0, rate)</c>, indexes this list.
    /// </summary>
    public static readonly IReadOnlyList<FailMode> Set =
    [
        new FailMode(FailKind.TtsFailed),
        new FailMode(FailKind.AssemblyFailed),
        new FailMode(FailKind.NodeError),
        new FailMode(FailKind.LostTask),
    ];

    /// <summary>Parses the environment value; blank means <c>0</c>. Anything outside <c>[0,1]</c> is an error.</summary>
    public static bool TryParse(string? raw, out double rate, out string? error)
    {
        rate = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var value = raw.Trim();
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate)
            || double.IsNaN(rate)
            || rate is < 0 or > 1)
        {
            rate = 0;
            error = $"MOCK_FAIL_RATE='{value}' must be a number between 0 and 1";
            return false;
        }

        return true;
    }

    /// <summary>The uniform value in <c>[0,1)</c> the <paramref name="renderJobId"/> hashes to (SHA-256, first 8 bytes).</summary>
    public static double Draw(Guid renderJobId)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.ASCII.GetBytes(renderJobId.ToString("N", CultureInfo.InvariantCulture)), hash);
        var value = BinaryPrimitives.ReadUInt64BigEndian(hash);

        // 53 significant bits keep the quotient exactly representable and strictly below 1.
        return (value >> 11) / (double)(1UL << 53);
    }

    /// <summary>The mode a rate-selected failure applies to this submit, or null when the draw does not fire.</summary>
    public static FailMode? Select(double rate, Guid renderJobId)
    {
        if (rate <= 0)
        {
            return null;
        }

        var draw = Draw(renderJobId);
        if (draw >= rate)
        {
            return null;
        }

        var index = Math.Min((int)(draw / rate * Set.Count), Set.Count - 1);
        return Set[index];
    }
}
