using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Iutq.Core.Primitives;

/// <summary>
///     Hard caps of blob format v2. Every count and index is a 16-bit field,
///     ticks are 16-bit, and payload offsets address the arena in 4-byte units.
///     Raising a cap means widening the field and bumping the blob version;
///     the runtime structs (TimelineIndex, ClipFrame, ...) stay 32/64-bit.
/// </summary>
public static class Format
{
    /// <summary>Payload arena addressing granularity in bytes.</summary>
    public const int PayloadUnit = 4;

    public const int MaxTimelines = ushort.MaxValue;
    public const int MaxTracks = ushort.MaxValue;
    public const int MaxTrackTemplates = ushort.MaxValue;
    public const int MaxClips = ushort.MaxValue;
    public const int MaxBoundaries = ushort.MaxValue;
    public const int MaxTypes = ushort.MaxValue;
    public const int MaxTimelineDuration = ushort.MaxValue;
    public const int MaxBinding = ushort.MaxValue;
    public const int MaxPayloadBytes = ushort.MaxValue * PayloadUnit;
}

public readonly record struct TimelineKey(ulong Value)
{
    /// <summary>
    ///     Derives a stable key from a name (FNV-1a 64 over the UTF-8 bytes).
    ///     Deterministic across processes and machines; zero is remapped to 1
    ///     because 0 is reserved as the invalid key.
    /// </summary>
    public static TimelineKey FromName(string name)
    {
        var hash = Fnv1A64.HashString(name);

        return new TimelineKey(hash == 0 ? 1 : hash);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineIndex
{
    public readonly int Value;

    public TimelineIndex(int value)
    {
        Value = value;
    }

    public static readonly TimelineIndex None = new(-1);

    public bool IsValid => Value >= 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct BindingId
{
    public readonly int Value;

    public BindingId(int value)
    {
        Value = value;
    }

    public static readonly BindingId None = new(-1);
}

[SuppressMessage("ReSharper", "UnusedTypeParameter",
    Justification =
        "Phantom type parameter: statically binds a clip type to its payload type with no runtime representation.")]
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct ClipType<T>
    where T : unmanaged
{
    public readonly ulong Key;

    public ClipType(ulong key)
    {
        Check.TypeKeyNonZero(key);

        Key = key;
    }

    /// <summary>
    ///     Derives a stable type key from a name (FNV-1a 64 over the UTF-8 bytes).
    ///     Deterministic across processes and machines; zero is remapped to 1
    ///     because 0 is reserved as the invalid key.
    /// </summary>
    [SuppressMessage("Performance", "CA1000:Do not declare static members on generic types",
        Justification = "The factory must be generic to construct the phantom-typed ClipType<T>; a non-generic counterpart could not bind T.")]
    public static ClipType<T> FromName(string name)
    {
        var hash = Fnv1A64.HashString(name);

        return new ClipType<T>(hash == 0 ? 1 : hash);
    }
}

[SuppressMessage("ReSharper", "UnusedTypeParameter",
    Justification =
        "Phantom type parameter: statically binds a handle to its payload type with no runtime representation.")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ClipTypeHandle<T>
    where T : unmanaged
{
    private readonly int _slotPlusOne;

    internal ClipTypeHandle(int typeSlot)
    {
        _slotPlusOne = Check.SlotPlusOne(typeSlot);
    }

    internal int TypeSlot => _slotPlusOne - 1;

    public bool IsValid => _slotPlusOne != 0;
}
