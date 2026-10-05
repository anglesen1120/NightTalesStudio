using System.Buffers;
using System.Text;

namespace NightTales.Domain.Stories.Similarity;

/// <summary>Hashes whitespace-delimited token windows using UTF-8 and fixed seed-zero XXH64.</summary>
public static class Shingler
{
    /// <summary>Returns one hash per window, including duplicates, or none when fewer than n tokens.</summary>
    public static ulong[] Hash(string normalized, int n) => Windows(normalized, n).ToArray();

    /// <summary>Returns the distinct hashes of the supplied token windows.</summary>
    public static IReadOnlySet<ulong> Shingles(ReadOnlySpan<string> tokens, int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        if (tokens.Length < n)
        {
            return new HashSet<ulong>();
        }

        var byteCount = tokens.Length - 1;
        foreach (var token in tokens)
        {
            byteCount = checked(byteCount + Encoding.UTF8.GetByteCount(token));
        }

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        var ends = ArrayPool<int>.Shared.Rent(tokens.Length);
        try
        {
            var position = 0;
            for (var i = 0; i < tokens.Length; i++)
            {
                if (i > 0)
                {
                    buffer[position++] = 0x1f;
                }

                position += Encoding.UTF8.GetBytes(tokens[i].AsSpan(), buffer.AsSpan(position));
                ends[i] = position;
            }

            var result = new HashSet<ulong>();
            for (var i = 0; i <= tokens.Length - n; i++)
            {
                var start = i == 0 ? 0 : ends[i - 1] + 1;
                result.Add(XxHash64.HashToUInt64(buffer.AsSpan(start, ends[i + n - 1] - start)));
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            ArrayPool<int>.Shared.Return(ends);
        }
    }

    // Streaming the hashes avoids allocating any corpus hash array or corpus-sized hash set.
    internal static IEnumerable<ulong> Windows(string normalized, int n)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        if (normalized.Length < n)
        {
            yield break;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(normalized.Length));
        var starts = new int[n];
        try
        {
            var position = 0;
            var offset = 0;
            var tokenCount = 0;
            while (offset < normalized.Length)
            {
                while (offset < normalized.Length && char.IsWhiteSpace(normalized[offset]))
                {
                    offset++;
                }

                var start = offset;
                while (offset < normalized.Length && !char.IsWhiteSpace(normalized[offset]))
                {
                    offset++;
                }

                if (start == offset)
                {
                    break;
                }

                if (tokenCount > 0)
                {
                    buffer[position++] = 0x1f;
                }

                starts[tokenCount % n] = position;
                position += Encoding.UTF8.GetBytes(normalized.AsSpan(start, offset - start), buffer.AsSpan(position));
                tokenCount++;
                if (tokenCount >= n)
                {
                    var windowStart = starts[(tokenCount - n) % n];
                    yield return XxHash64.HashToUInt64(buffer.AsSpan(windowStart, position - windowStart));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
