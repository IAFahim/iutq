using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iutq.Core;

internal sealed class ArenaBuilder
{
    private readonly Dictionary<ArenaKey, List<int>> _buckets = [];
    private readonly List<byte> _storage = [];

    public int Intern<T>(in T value)
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        int alignment = Alignment(size);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1));

        ulong hash = Fnv1a64.Hash(bytes);
        ArenaKey key = new(hash, size);

        if (_buckets.TryGetValue(key, out List<int>? candidates))
        {
            Span<byte> storage = CollectionsMarshal.AsSpan(_storage);

            foreach (int candidate in candidates)
            {
                if (bytes.SequenceEqual(storage.Slice(candidate, size)))
                {
                    return candidate;
                }
            }
        }
        else
        {
            candidates = [];
            _buckets.Add(key, candidates);
        }

        int alignedOffset = AlignUp(_storage.Count, alignment);

        while (_storage.Count < alignedOffset)
        {
            _storage.Add(0);
        }

        int offset = _storage.Count;

        foreach (byte valueByte in bytes)
        {
            _storage.Add(valueByte);
        }

        candidates.Add(offset);
        return offset;
    }

    public byte[] Build() => [.. _storage];

    private static int Alignment(int size) =>
        size % 8 == 0 ? 8 :
        size % 4 == 0 ? 4 :
        size % 2 == 0 ? 2 :
        1;

    private static int AlignUp(int value, int alignment) =>
        (value + alignment - 1) & -alignment;

    private readonly record struct ArenaKey(ulong Hash, int Size);
}
