using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Iutq.Core.Primitives;

namespace Iutq.Core.Baking;

internal sealed class ArenaBuilder
{
    private readonly Dictionary<(ulong Hash, int Size), List<int>> _buckets = [];
    private readonly List<byte> _storage = [];

    public int Intern<T>(in T value) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.AsRef(in value), 1)
        );

        var hash = Fnv1A64.Hash(bytes);
        (ulong Hash, int Size) key = (hash, size);
        if (_buckets.TryGetValue(key, out var candidates))
        {
            var storage = CollectionsMarshal.AsSpan(_storage);
            foreach (var candidate in candidates)
                if (bytes.SequenceEqual(storage.Slice(candidate, size))) return candidate;
        }
        else
        {
            candidates = [];
            _buckets.Add(key, candidates);
        }

        if (_storage.Count + size > Format.MaxPayloadBytes)
            throw new InvalidOperationException(
                $"iutq blob v2 cap exceeded: payload arena is limited to {Format.MaxPayloadBytes} bytes " +
                $"(offsets are 16-bit in {Format.PayloadUnit}-byte units).");

        var alignedOffset = AlignUp(_storage.Count, Format.PayloadUnit);
        while (_storage.Count < alignedOffset) _storage.Add(0);

        var offset = _storage.Count;
        foreach (var valueByte in bytes) _storage.Add(valueByte);

        candidates.Add(offset);
        return offset;
    }

    public byte[] Build()
    {
        var padded = AlignUp(_storage.Count, Format.PayloadUnit);
        while (_storage.Count < padded) _storage.Add(0);
        return [.. _storage];
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & -alignment;
}
