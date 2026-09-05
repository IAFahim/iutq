using System.Runtime.InteropServices;
using Iutq.Core.Primitives;

namespace Iutq.Core.Storage;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineHeader : IEquatable<TimelineHeader>
{
    public readonly ushort Duration;
    public readonly TimelineFlags Flags;

    public TimelineHeader(ushort duration, TimelineFlags flags)
    {
        Duration = duration;
        Flags = flags;
    }

    public bool Equals(TimelineHeader other)
    {
        return Duration == other.Duration && Flags == other.Flags;
    }

    public override bool Equals(object? obj)
    {
        return obj is TimelineHeader other && Equals(other);
    }

    public static bool operator ==(TimelineHeader left, TimelineHeader right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TimelineHeader left, TimelineHeader right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Duration, Flags);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineLookupEntry : IEquatable<TimelineLookupEntry>
{
    public readonly ulong Key;
    public readonly ushort TimelineIndex;

    public TimelineLookupEntry(ulong key, ushort timelineIndex)
    {
        Key = key;
        TimelineIndex = timelineIndex;
    }

    public bool Equals(TimelineLookupEntry other)
    {
        return Key == other.Key && TimelineIndex == other.TimelineIndex;
    }

    public override bool Equals(object? obj)
    {
        return obj is TimelineLookupEntry other && Equals(other);
    }

    public static bool operator ==(TimelineLookupEntry left, TimelineLookupEntry right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TimelineLookupEntry left, TimelineLookupEntry right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Key, TimelineIndex);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TrackInstance : IEquatable<TrackInstance>
{
    public readonly ushort Binding;
    public readonly ushort TrackTemplateId;

    public TrackInstance(ushort binding, ushort trackTemplateId)
    {
        Binding = binding;
        TrackTemplateId = trackTemplateId;
    }

    public bool Equals(TrackInstance other)
    {
        return Binding == other.Binding && TrackTemplateId == other.TrackTemplateId;
    }

    public override bool Equals(object? obj)
    {
        return obj is TrackInstance other && Equals(other);
    }

    public static bool operator ==(TrackInstance left, TrackInstance right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TrackInstance left, TrackInstance right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Binding, TrackTemplateId);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TrackTemplate : IEquatable<TrackTemplate>
{
    public readonly ushort TypeSlot;
    public readonly ushort ClipStart;
    public readonly ushort ClipCount;
    public readonly ushort LaneSplit;
    public readonly ushort BoundaryStart;
    public readonly ushort BoundaryCount;
    public readonly TrackMode Mode;

    public TrackTemplate(
        ushort typeSlot,
        ushort clipStart,
        ushort clipCount,
        ushort laneSplit,
        ushort boundaryStart,
        ushort boundaryCount,
        TrackMode mode)
    {
        TypeSlot = typeSlot;
        ClipStart = clipStart;
        ClipCount = clipCount;
        LaneSplit = laneSplit;
        BoundaryStart = boundaryStart;
        BoundaryCount = boundaryCount;
        Mode = mode;
    }

    public bool Equals(TrackTemplate other)
    {
        return TypeSlot == other.TypeSlot &&
               ClipStart == other.ClipStart &&
               ClipCount == other.ClipCount &&
               LaneSplit == other.LaneSplit &&
               BoundaryStart == other.BoundaryStart &&
               BoundaryCount == other.BoundaryCount &&
               Mode == other.Mode;
    }

    public override bool Equals(object? obj)
    {
        return obj is TrackTemplate other && Equals(other);
    }

    public static bool operator ==(TrackTemplate left, TrackTemplate right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TrackTemplate left, TrackTemplate right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(TypeSlot, ClipStart, ClipCount, LaneSplit, BoundaryStart, BoundaryCount, Mode);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ClipEntry : IEquatable<ClipEntry>
{
    public readonly ushort Start;
    public readonly ushort End;

    /// <summary>Payload arena offset in <see cref="Primitives.Format.PayloadUnit" /> byte units.</summary>
    public readonly ushort DataOffset;
    public readonly ClipEase Ease;

    public ClipEntry(ushort start, ushort end, ushort dataOffset, ClipEase ease)
    {
        Start = start;
        End = end;
        DataOffset = dataOffset;
        Ease = ease;
    }

    public bool Equals(ClipEntry other)
    {
        return Start == other.Start && End == other.End && DataOffset == other.DataOffset && Ease == other.Ease;
    }

    public override bool Equals(object? obj)
    {
        return obj is ClipEntry other && Equals(other);
    }

    public static bool operator ==(ClipEntry left, ClipEntry right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ClipEntry left, ClipEntry right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Start, End, DataOffset, Ease);
    }

    public int Duration => End - Start;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct TrackBoundary : IEquatable<TrackBoundary>
{
    /// <summary>Sentinel for clip-only boundaries; also outside the valid clip index range by one.</summary>
    internal const ushort NoClip = ushort.MaxValue;

    public readonly ushort Tick;
    public readonly ushort ClipA;
    public readonly ushort ClipB;
    public readonly BoundaryKind Kind;

    public TrackBoundary(ushort tick, ushort clipA, ushort clipB, BoundaryKind kind)
    {
        Tick = tick;
        ClipA = clipA;
        ClipB = clipB;
        Kind = kind;
    }

    public bool Equals(TrackBoundary other)
    {
        return Tick == other.Tick && ClipA == other.ClipA && ClipB == other.ClipB && Kind == other.Kind;
    }

    public override bool Equals(object? obj)
    {
        return obj is TrackBoundary other && Equals(other);
    }

    public static bool operator ==(TrackBoundary left, TrackBoundary right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TrackBoundary left, TrackBoundary right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Tick, ClipA, ClipB, Kind);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TypeDescriptor : IEquatable<TypeDescriptor>
{
    public readonly ulong Key;
    public readonly ushort Size;

    public TypeDescriptor(ulong key, ushort size)
    {
        Key = key;
        Size = size;
    }

    public bool Equals(TypeDescriptor other)
    {
        return Key == other.Key && Size == other.Size;
    }

    public override bool Equals(object? obj)
    {
        return obj is TypeDescriptor other && Equals(other);
    }

    public static bool operator ==(TypeDescriptor left, TypeDescriptor right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TypeDescriptor left, TypeDescriptor right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Key, Size);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TrackSpan : IEquatable<TrackSpan>
{
    public readonly ushort TrackStart;
    public readonly ushort TrackCount;

    public TrackSpan(ushort trackStart, ushort trackCount)
    {
        TrackStart = trackStart;
        TrackCount = trackCount;
    }

    public bool Equals(TrackSpan other)
    {
        return TrackStart == other.TrackStart && TrackCount == other.TrackCount;
    }

    public override bool Equals(object? obj)
    {
        return obj is TrackSpan other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(TrackStart, TrackCount);
    }

    public static bool operator ==(TrackSpan left, TrackSpan right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TrackSpan left, TrackSpan right)
    {
        return !left.Equals(right);
    }
}
