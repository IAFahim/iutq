using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iutq.Core;

internal sealed class ArenaBuilder
{
    private readonly Dictionary<(ulong Hash, int Size), List<int>> _buckets = [];
    private readonly List<byte> _storage = [];

    public int Intern<T>(in T value)
        where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var alignment = Alignment(size);
        var bytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1));

        var hash = Fnv1A64.Hash(bytes);
        (ulong Hash, int Size) key = (hash, size);

        if (_buckets.TryGetValue(key, out var candidates))
        {
            var storage = CollectionsMarshal.AsSpan(_storage);

            foreach (var candidate in candidates)
                if (bytes.SequenceEqual(storage.Slice(candidate, size)))
                    return candidate;
        }
        else
        {
            candidates = [];
            _buckets.Add(key, candidates);
        }

        var alignedOffset = AlignUp(_storage.Count, alignment);

        while (_storage.Count < alignedOffset) _storage.Add(0);

        var offset = _storage.Count;

        foreach (var valueByte in bytes) _storage.Add(valueByte);

        candidates.Add(offset);
        return offset;
    }

    public byte[] Build()
    {
        return [.. _storage];
    }

    private static int Alignment(int size)
    {
        return size % 8 == 0 ? 8 :
            size % 4 == 0 ? 4 :
            size % 2 == 0 ? 2 :
            1;
    }

    private static int AlignUp(int value, int alignment)
    {
        return (value + alignment - 1) & -alignment;
    }
}