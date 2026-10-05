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
            buffer.AsSpan(0, byteCount).Clear();
            ArrayPool<byte>.Shared.Return(buffer);
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

        var starts = new int[n];
        var offset = 0;
        var tokenCount = 0;
        var windowBytes = 0;
        var maxWindowBytes = 0;
        // Reuse the ring as byte lengths for this sizing pass, then as offsets for hashing.
        while (ReadToken(normalized, ref offset, out var start, out var length))
        {
            var bytes = Encoding.UTF8.GetByteCount(normalized.AsSpan(start, length));
            var slot = tokenCount % n;
            if (tokenCount >= n)
            {
                windowBytes -= starts[slot];
            }

            starts[slot] = bytes;
            windowBytes = checked(windowBytes + bytes);
            tokenCount++;
            if (tokenCount >= n)
            {
                maxWindowBytes = Math.Max(maxWindowBytes, checked(windowBytes + n - 1));
            }
        }

        if (tokenCount < n)
        {
            yield break;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(maxWindowBytes);
        var written = 0;
        try
        {
            var position = 0;
            offset = 0;
            tokenCount = 0;
            while (ReadToken(normalized, ref offset, out var start, out var length))
            {
                var bytes = Encoding.UTF8.GetByteCount(normalized.AsSpan(start, length));
                var separatorBytes = tokenCount > 0 && n > 1 ? 1 : 0;
                if (bytes + separatorBytes > buffer.Length - position)
                {
                    // Retain only the previous n-1 tokens. Compact only when the buffer fills,
                    // rather than copying a window after every hash; n=1 retains nothing.
                    var retainedStart = n == 1 ? position : starts[(tokenCount - n + 1) % n];
                    buffer.AsSpan(retainedStart, position - retainedStart).CopyTo(buffer);
                    position -= retainedStart;
                    for (var i = Math.Max(0, tokenCount - n + 1); i < tokenCount; i++)
                    {
                        starts[i % n] -= retainedStart;
                    }
                }

                if (separatorBytes > 0)
                {
                    buffer[position++] = 0x1f;
                }

                starts[tokenCount % n] = position;
                position += Encoding.UTF8.GetBytes(normalized.AsSpan(start, length), buffer.AsSpan(position));
                written = Math.Max(written, position);
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
            buffer.AsSpan(0, written).Clear();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool ReadToken(string text, ref int offset, out int start, out int length)
    {
        while (offset < text.Length && char.IsWhiteSpace(text[offset]))
        {
            offset++;
        }

        start = offset;
        while (offset < text.Length && !char.IsWhiteSpace(text[offset]))
        {
            offset++;
        }

        length = offset - start;
        return length > 0;
    }
}
