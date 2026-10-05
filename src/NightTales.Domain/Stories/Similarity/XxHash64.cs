using System.Buffers.Binary;
using System.Numerics;

namespace NightTales.Domain.Stories.Similarity;

/// <summary>Bit-exact XXH64 with the reference algorithm's unsigned arithmetic.</summary>
internal static class XxHash64
{
    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    internal static ulong HashToUInt64(ReadOnlySpan<byte> data, long seed = 0)
    {
        unchecked
        {
            var unsignedSeed = (ulong)seed;
            var offset = 0;
            ulong hash;
            if (data.Length >= 32)
            {
                var v1 = unsignedSeed + Prime1 + Prime2;
                var v2 = unsignedSeed + Prime2;
                var v3 = unsignedSeed;
                var v4 = unsignedSeed - Prime1;
                do
                {
                    v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                    v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 8)..]));
                    v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 16)..]));
                    v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 24)..]));
                    offset += 32;
                }
                while (offset <= data.Length - 32);

                hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7)
                    + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
                hash = MergeRound(hash, v1);
                hash = MergeRound(hash, v2);
                hash = MergeRound(hash, v3);
                hash = MergeRound(hash, v4);
            }
            else
            {
                hash = unsignedSeed + Prime5;
            }

            hash += (ulong)data.Length;
            while (offset <= data.Length - 8)
            {
                hash ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                hash = BitOperations.RotateLeft(hash, 27) * Prime1 + Prime4;
                offset += 8;
            }

            if (offset <= data.Length - 4)
            {
                hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) * Prime1;
                hash = BitOperations.RotateLeft(hash, 23) * Prime2 + Prime3;
                offset += 4;
            }

            while (offset < data.Length)
            {
                hash ^= data[offset++] * Prime5;
                hash = BitOperations.RotateLeft(hash, 11) * Prime1;
            }

            hash ^= hash >> 33;
            hash *= Prime2;
            hash ^= hash >> 29;
            hash *= Prime3;
            return hash ^ (hash >> 32);
        }
    }

    private static ulong Round(ulong accumulator, ulong value) =>
        unchecked(BitOperations.RotateLeft(accumulator + value * Prime2, 31) * Prime1);

    private static ulong MergeRound(ulong accumulator, ulong value) =>
        unchecked((accumulator ^ Round(0, value)) * Prime1 + Prime4);
}
